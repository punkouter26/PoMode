// Live-page session: microphone → pitch detection → note events, ready for the server to analyze.
// State is mirrored to data-live-* attributes for the Playwright suite, matching mixer.js's contract.

import { detectPitch, openMicrophone, createNoteCollector } from './live-pitch.js';

const sessions = new Map();

export async function start(root) {
    stop(root);
    const session = { mic: null, collector: createNoteCollector(), startTime: null, lastTime: 0 };
    try {
        session.mic = await openMicrophone((samples, sampleRate, contextTime) => {
            session.startTime ??= contextTime;
            const t = contextTime - session.startTime;
            session.lastTime = t;
            session.collector.push(detectPitch(samples, sampleRate), t);
            root.dataset.liveSeconds = t.toFixed(1);
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
    sessions.delete(root);
    root.dataset.live = 'off';
}
