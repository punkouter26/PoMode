// Live-page session: microphone → pitch detection → note events, ready for the server to analyze.
// State is mirrored to data-live-* attributes for the Playwright suite, matching mixer.js's contract.

import { detectPitch, openMicrophone, createNoteCollector } from './live-pitch.js';
import { encodeWav } from './wav.js';

const sessions = new Map();

// The finished take survives stop(), so the recording can still be sent to the analyzer after the
// microphone is released. One per page; a new start() discards the previous one.
const takes = new Map();

// The pitch tracker only needs note events, but the analyzer wants real audio, so the raw frames are
// kept too. Capped so a microphone left running cannot grow without limit — three minutes of 48 kHz
// mono is roughly 17 MB once encoded, well inside the upload limit.
const MAX_CAPTURE_SECONDS = 180;

export async function start(root) {
    stop(root);
    takes.delete(root);
    const session = {
        mic: null, collector: createNoteCollector(), startTime: null, lastTime: 0,
        pcm: [], frames: 0, sampleRate: 0,
    };
    try {
        session.mic = await openMicrophone((samples, sampleRate, contextTime) => {
            session.startTime ??= contextTime;
            const t = contextTime - session.startTime;
            session.lastTime = t;
            session.collector.push(detectPitch(samples, sampleRate), t);

            // Copy: the callback is handed the live buffer, which is reused on the next frame.
            session.sampleRate = sampleRate;
            if (session.frames < sampleRate * MAX_CAPTURE_SECONDS) {
                session.pcm.push(samples.slice());
                session.frames += samples.length;
            }

            root.dataset.liveSeconds = t.toFixed(1);
            root.dataset.liveCaptured = (session.frames / sampleRate).toFixed(1);
        });
    } catch {
        root.dataset.live = 'denied';
        return false;
    }
    sessions.set(root, session);
    root.dataset.live = 'on';
    root.dataset.liveSeconds = '0';
    return true;
}

/// The note events captured so far (times rebased to zero). Safe to call repeatedly while running.
export function snapshot(root) {
    const session = sessions.get(root);
    return session ? session.collector.snapshot(session.lastTime) : [];
}

export function stop(root) {
    const session = sessions.get(root);
    if (!session) {
        return;
    }
    session.mic?.stop();
    if (session.frames > 0) {
        takes.set(root, { pcm: session.pcm, sampleRate: session.sampleRate, frames: session.frames });
    }
    sessions.delete(root);
    root.dataset.live = 'off';
}

/// The take as a 16-bit PCM WAV, ready to upload. Works while listening and after stopping, and
/// returns an empty array when nothing has been recorded.
export function takeWavBytes(root) {
    const source = sessions.get(root) ?? takes.get(root);
    if (!source || source.frames === 0) {
        return new Uint8Array(0);
    }
    return encodeWav(source.pcm, source.frames, source.sampleRate);
}


