// Resumable audio upload over tus: the transport half of every way audio reaches the analyzer from
// the home page — a picked file, a microphone recording, a file shared in from another app.
//
// The problem it solves is a phone on mobile data. A voice memo is up to 100 MB, and a single POST
// that drops at 60 MB used to start again from zero. Over tus the server keeps what arrived, and on
// any failure this module asks it how far it got (a HEAD) and sends only the rest. It retries a
// dropped connection on its own, including while the browser reports being offline — the default
// gives up the moment `navigator.onLine` is false, which on a train is most of the time.
//
// A file picked from disk also survives a reload: tus-js-client keeps a fingerprint of the file in
// localStorage, and choosing the same file again resumes the upload it left. A recording or a shared
// file is a Blob with no name or date to fingerprint, so those resume within the session only.
//
// Finishing the upload does not start the analysis. This resolves with the upload's id and the page
// asks the server to analyse it, so the executor picks and the returned job status stay in C#.

import './tus.min.js';
import { encodeWav } from '../capture/wav.js';

const tus = window.tus;

const ENDPOINT = '/api/uploads';
const MAX_BYTES = 100 * 1024 * 1024;

/// 8 MB PATCHes. One PATCH for the whole file would also resume, but a proxy in front of the app may
/// buffer a request body before forwarding any of it, and then a drop loses the whole attempt.
const CHUNK_BYTES = 8 * 1024 * 1024;

/// Roughly two and a half minutes of trying before the page says the upload stopped. Past that the
/// person is better told, and a picked file can still be resumed by choosing it again.
const RETRY_DELAYS = [0, 1000, 3000, 5000, 10000, 15000, 20000, 30000, 30000, 30000];

/// Uploads `source` — a file input element, a Blob, or the bytes of a recording — and resolves with
/// the upload id once the server holds every byte. Progress goes to `dotNet.OnUploadProgress(sent,
/// total, resuming)`, at most once per whole percent. Rejects with a message fit to show as-is.
export async function upload(source, name, dotNet) {
    const picked = toBlob(source);
    if (!picked || picked.size === 0) {
        throw new Error('The file is empty.');
    }
    const file = await asWavOrMp3(picked);
    if (file.size > MAX_BYTES) {
        // Refused before a byte is sent; the server would refuse it at creation anyway.
        throw new Error('File exceeds the 100 MB limit.');
    }
    const fileName = file === picked ? (name || file.name || 'audio') : wavName(name || picked.name);
    const resumable = typeof File !== 'undefined' && file instanceof File;
    // A recording, a shared file or a converted one exists nowhere but this page's memory. It is
    // kept in IndexedDB until the server has it, so a reload or a discarded tab can carry on with
    // it (takeUnsent) under the same fingerprint, from the byte the server last confirmed.
    if (!resumable) {
        await stash({ blob: file, name: fileName });
    }

    let resuming = false;
    let lastSent = 0;
    let lastPercent = -1;
    const report = (sent, total) => {
        const percent = total > 0 ? Math.floor((sent / total) * 100) : 0;
        if (percent === lastPercent) {
            return;
        }
        lastPercent = percent;
        try { dotNet?.invokeMethodAsync('OnUploadProgress', sent, total, resuming); } catch { }
    };

    return await new Promise((resolve, reject) => {
        const task = new tus.Upload(file, {
            endpoint: ENDPOINT,
            chunkSize: CHUNK_BYTES,
            retryDelays: RETRY_DELAYS,
            metadata: { filename: fileName, filetype: file.type || 'application/octet-stream' },
            storeFingerprintForResuming: true,
            ...(resumable ? {} : { fingerprint: async () => `pm-unsent-${file.size}-${fileName}` }),
            removeFingerprintOnSuccess: true,
            onShouldRetry: (error) => {
                const status = error?.originalResponse?.getStatus?.() ?? 0;
                // A 4xx is the server saying no — too large, signed out, not yours — and asking again
                // gets the same answer. 409 and 423 are the protocol's own "try again" codes.
                const retry = status < 400 || status >= 500 || status === 409 || status === 423;
                if (retry) {
                    resuming = true;
                    lastPercent = -1;
                    report(lastSent, file.size);
                }
                return retry;
            },
            onProgress: (sent, total) => {
                lastSent = sent;
                report(sent, total);
            },
            onChunkComplete: () => {
                // Bytes the server has confirmed: whatever went wrong has recovered.
                if (resuming) {
                    resuming = false;
                    lastPercent = -1;
                }
            },
            onSuccess: () => {
                report(file.size, file.size);
                stash(null);
                resolve(task.url.substring(task.url.lastIndexOf('/') + 1));
            },
            onError: (error) => {
                const status = error?.originalResponse?.getStatus?.() ?? 0;
                if (status >= 400 && status < 500) {
                    stash(null); // refused outright: offering it again next visit would only repeat that
                }
                reject(new Error(describe(error, resumable)));
            },
        });

        task.findPreviousUploads()
            .then((previous) => {
                if (previous.length > 0) {
                    // The server may have expired or refused it since; tus-js-client then starts a
                    // fresh upload by itself, so a stale fingerprint costs one HEAD and nothing more.
                    resuming = true;
                    task.resumeFromPreviousUpload(previous[0]);
                }
                task.start();
            })
            .catch(() => task.start());
    });
}

const UNSENT_DB = 'pm-uploads';
const UNSENT_STORE = 'unsent';

/// One slot: the latest recording that has not reached the server, or nothing. Every failure here
/// is swallowed, because a browser without IndexedDB only loses the resume, not the upload.
function unsentStore(mode, run) {
    return new Promise((resolve) => {
        try {
            const open = indexedDB.open(UNSENT_DB, 1);
            open.onupgradeneeded = () => open.result.createObjectStore(UNSENT_STORE);
            open.onerror = () => resolve(null);
            open.onsuccess = () => {
                const request = run(open.result.transaction(UNSENT_STORE, mode).objectStore(UNSENT_STORE));
                request.onsuccess = () => resolve(request.result ?? null);
                request.onerror = () => resolve(null);
            };
        } catch {
            resolve(null);
        }
    });
}

function stash(entry) {
    return unsentStore('readwrite', (store) => (entry ? store.put(entry, 0) : store.delete(0)));
}

let unsent = null;

/// The name of a recording left unsent by an earlier visit, or null. The Blob itself is handed over
/// by takeUnsent, as a reference, the way the share target's file is.
export async function unsentName() {
    unsent = await unsentStore('readonly', (store) => store.get(0));
    return unsent?.blob instanceof Blob ? unsent.name : null;
}

export function takeUnsent() {
    const blob = unsent?.blob ?? null;
    unsent = null;
    return blob;
}

function toBlob(source) {
    if (typeof HTMLInputElement !== 'undefined' && source instanceof HTMLInputElement) {
        const picked = source.files && source.files[0];
        // Cleared so choosing the same file again (to resume it, or analyse it twice) still fires
        // a change event. The File object stays readable after its input is reset.
        source.value = '';
        return picked || null;
    }
    if (source instanceof Blob) {
        return source;
    }
    if (source instanceof Uint8Array) {
        return new Blob([source], { type: 'audio/wav' });
    }
    return null;
}

/// The server takes WAV and MP3, by their first bytes. Anything else the browser can decode (a
/// phone's .m4a voice memo, most often) is decoded here and sent as WAV instead, so every way in
/// accepts it and the server's rule stays WAV or MP3. The price is that such a file is a Blob, not
/// the picked File, and so resumes within the session only.
async function asWavOrMp3(file) {
    const head = new Uint8Array(await file.slice(0, 12).arrayBuffer());
    const text = (from, to) => String.fromCharCode(...head.subarray(from, to));
    const isWav = text(0, 4) === 'RIFF' && text(8, 12) === 'WAVE';
    // Frame sync plus a non-zero layer: raw AAC (ADTS) has the same sync bits and layer 00.
    const isMp3 = text(0, 3) === 'ID3'
        || (head[0] === 0xFF && (head[1] & 0xE0) === 0xE0 && (head[1] & 0x06) !== 0);
    if (isWav || isMp3) {
        return file;
    }

    let bytes;
    try {
        bytes = await file.arrayBuffer();
    } catch {
        throw new Error('The file could not be read from disk. Choose it again.');
    }
    // Offline: decoding needs no audio device, and its rate is the rate the WAV is written at
    // (a live context decodes to the hardware's, usually 48 kHz, which only makes the file bigger).
    const ctx = new OfflineAudioContext(2, 1, 44100);
    let buffer;
    try {
        buffer = await ctx.decodeAudioData(bytes);
    } catch (error) {
        // The decoder is the browser's own. Chrome, Edge and Safari carry AAC; an editor's built-in
        // browser and some Linux Chromium builds do not. An MP4 container (.m4a) then goes up as it
        // is, and the server converts it if it has an ffmpeg, or refuses it in words if it has not.
        console.warn('decodeAudioData refused the file', error);
        if (text(4, 8) === 'ftyp') {
            return file;
        }
        throw new Error('This browser cannot decode that file. Convert it to WAV or MP3.');
    }
    // A compressed file grows about tenfold as WAV. Stereo that would pass the cap goes up as it
    // is; past it the channels are averaged to one, and past that the file is refused for what it
    // is (too long), not for a size the picked file never had.
    const wavBytes = (count) => 44 + buffer.length * count * 2;
    const stereo = buffer.numberOfChannels >= 2 && wavBytes(2) <= MAX_BYTES;
    if (!stereo && wavBytes(1) > MAX_BYTES) {
        const minutes = Math.floor((MAX_BYTES - 44) / 2 / buffer.sampleRate / 60);
        throw new Error(`That recording is too long to convert here (over ${minutes} minutes). Save it as MP3 and try again.`);
    }
    const channels = stereo ? 2 : 1;
    const sources = Math.min(2, buffer.numberOfChannels);
    const interleaved = new Float32Array(buffer.length * channels);
    for (let channel = 0; channel < sources; channel++) {
        const data = buffer.getChannelData(channel);
        if (stereo) {
            for (let i = 0; i < data.length; i++) interleaved[i * 2 + channel] = data[i];
        } else {
            for (let i = 0; i < data.length; i++) interleaved[i] += data[i] / sources;
        }
    }
    return new Blob([encodeWav([interleaved], buffer.length, buffer.sampleRate, channels)], { type: 'audio/wav' });
}

function wavName(name) {
    return `${(name || 'audio').replace(/\.[^.]*$/, '')}.wav`;
}

function describe(error, resumable) {
    const status = error?.originalResponse?.getStatus?.() ?? 0;
    if (status === 413) {
        return 'File exceeds the 100 MB limit.';
    }
    if (status === 401) {
        return 'Your session has ended. Reload the page and try again.';
    }
    if (status === 0) {
        return resumable
            ? 'The connection dropped and did not come back. Choose the file again to carry on from where it stopped.'
            : 'The connection dropped and did not come back. Please try again.';
    }
    return 'The upload failed. Please try again.';
}
