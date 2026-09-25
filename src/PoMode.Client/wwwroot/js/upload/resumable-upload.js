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
    const file = toBlob(source);
    if (!file || file.size === 0) {
        throw new Error('The file is empty.');
    }
    if (file.size > MAX_BYTES) {
        // Refused before a byte is sent; the server would refuse it at creation anyway.
        throw new Error('File exceeds the 100 MB limit.');
    }
    const fileName = name || file.name || 'audio';
    const resumable = typeof File !== 'undefined' && file instanceof File;

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
            storeFingerprintForResuming: resumable,
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
                resolve(task.url.substring(task.url.lastIndexOf('/') + 1));
            },
            onError: (error) => reject(new Error(describe(error, resumable))),
        });

        (resumable ? task.findPreviousUploads() : Promise.resolve([]))
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
