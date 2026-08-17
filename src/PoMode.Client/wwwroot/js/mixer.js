// Web Audio stem mixer (spec §7): mix, vocals and instrumental played sample-synchronised through
// GainNodes, switched by 50 ms ramps so there are no pops and the position is never lost.
//
// This module also owns the transport clock. It drives the canvas playhead directly through
// canvas.js rather than round-tripping 60 times a second through Blazor, so playback costs no
// component renders. Blazor only hears about discrete events (loaded, mode changed, failed).

import { setPlayhead } from './canvas.js';

const states = new Map();

const STEMS = ['mix', 'vocals', 'instrumental'];

/// Which stem is audible in each mode. Everything keeps playing; only the gains change.
const MODE_GAINS = {
    full: { mix: 1, vocals: 0, instrumental: 0 },
    vocals: { mix: 0, vocals: 1, instrumental: 0 },
    backing: { mix: 0, vocals: 0, instrumental: 1 },
};

const RAMP_SECONDS = 0.05;

/// Mirrors mode/time/duration onto the element without touching the status. Seeking and switching
/// stems change neither the transport state nor the loaded state, so they must not overwrite it.
function refresh(state) {
    const dataset = state.root.dataset;
    dataset.mixerMode = state.mode;
    dataset.mixerTime = currentSeconds(state).toFixed(3);
    dataset.mixerDuration = state.duration.toFixed(3);
}

/// Only the transport verbs (load, play, pause, end) set the status.
function report(state, status) {
    state.root.dataset.mixerStatus = status;
    refresh(state);
}

function currentSeconds(state) {
    if (!state.context) {
        return state.offset;
    }
    // startSources schedules `when` a beat in the future, so between play() and that moment the
    // difference is negative and nothing has actually been heard yet — the position is still `offset`.
    // Without the floor the reported clock, and therefore the playhead, jumps backwards on play.
    const elapsed = state.playing ? Math.max(state.context.currentTime - state.startedAt, 0) : 0;
    return Math.min(Math.max(state.offset + elapsed, 0), state.duration);
}

function applyGains(state, immediate) {
    const targets = MODE_GAINS[state.mode] ?? MODE_GAINS.full;
    const now = state.context.currentTime;
    for (const stem of STEMS) {
        const node = state.gains[stem];
        if (!node) {
            continue;
        }
        const target = targets[stem];
        if (immediate) {
            node.gain.value = target;
            continue;
        }
        // Ramp from wherever the gain actually is right now, so a mid-ramp switch does not jump.
        node.gain.cancelScheduledValues(now);
        node.gain.setValueAtTime(node.gain.value, now);
        node.gain.linearRampToValueAtTime(target, now + RAMP_SECONDS);
    }
}

function stopSources(state) {
    for (const source of Object.values(state.sources)) {
        try {
            source.stop();
        } catch {
            // Already stopped; AudioBufferSourceNode throws rather than no-opping.
        }
        source.disconnect();
    }
    state.sources = {};
}

/// AudioBufferSourceNodes are single-use, so every play and every seek builds a fresh set. They are
/// all started with the same `when`, which is what keeps the three stems sample-synchronised.
function startSources(state, offsetSeconds) {
    stopSources(state);
    const when = state.context.currentTime + 0.02; // a beat of headroom so all three share a start
    for (const stem of STEMS) {
        const buffer = state.buffers[stem];
        if (!buffer) {
            continue;
        }
        const source = state.context.createBufferSource();
        source.buffer = buffer;
        source.connect(state.gains[stem]);
        source.start(when, Math.min(offsetSeconds, buffer.duration));
        state.sources[stem] = source;
    }
    state.startedAt = when;
    state.offset = offsetSeconds;
}

function tick(state) {
    if (!states.has(state.root)) {
        return;
    }
    if (state.playing) {
        const seconds = currentSeconds(state);
        setPlayhead(state.canvas, seconds);
        state.root.dataset.mixerTime = seconds.toFixed(3);
        if (seconds >= state.duration) {
            pauseInternal(state, state.duration);
            report(state, 'ended');
            return;
        }
    }
    state.frame = requestAnimationFrame(() => tick(state));
}

function pauseInternal(state, atSeconds) {
    state.offset = atSeconds;
    state.playing = false;
    stopSources(state);
}

// ---- exports ----

export function init(root, canvas) {
    dispose(root);
    states.set(root, {
        root,
        canvas,
        context: null,
        buffers: {},
        gains: {},
        sources: {},
        mode: 'full',
        offset: 0,
        startedAt: 0,
        duration: 0,
        playing: false,
        frame: null,
    });
    report(states.get(root), 'idle');
}

/// Fetches and decodes the stems. Returns the decoded duration, or 0 if nothing could be loaded.
/// A stem the pipeline never wrote is skipped rather than treated as a failure — the mix alone is
/// enough to play, it just means the solo buttons have nothing to solo.
export async function load(root, urls) {
    const state = states.get(root);
    if (!state) {
        return 0;
    }

    report(state, 'loading');
    state.context ??= new (window.AudioContext || window.webkitAudioContext)();

    const loaded = await Promise.all(STEMS.map(async stem => {
        const url = urls[stem];
        if (!url) {
            return [stem, null];
        }
        try {
            const response = await fetch(url);
            if (!response.ok) {
                return [stem, null];
            }
            return [stem, await state.context.decodeAudioData(await response.arrayBuffer())];
        } catch {
            return [stem, null]; // an undecodable stem must not break the whole mixer
        }
    }));

    state.buffers = {};
    state.gains = {};
    for (const [stem, buffer] of loaded) {
        if (!buffer) {
            continue;
        }
        state.buffers[stem] = buffer;
        const gain = state.context.createGain();
        gain.gain.value = 0;
        gain.connect(state.context.destination);
        state.gains[stem] = gain;
    }

    state.duration = Math.max(0, ...Object.values(state.buffers).map(buffer => buffer.duration));
    state.offset = 0;
    state.playing = false;
    if (state.duration === 0) {
        report(state, 'unavailable');
        return 0;
    }

    applyGains(state, true);
    report(state, 'ready');
    if (state.frame === null) {
        state.frame = requestAnimationFrame(() => tick(state));
    }
    return state.duration;
}

export async function play(root) {
    const state = states.get(root);
    if (!state || state.duration === 0 || state.playing) {
        return;
    }
    // Called from a real click, so the autoplay policy lets the context resume here.
    if (state.context.state === 'suspended') {
        await state.context.resume();
    }
    startSources(state, state.offset >= state.duration ? 0 : state.offset);
    state.playing = true;
    applyGains(state, false);
    report(state, 'playing');
}

export function pause(root) {
    const state = states.get(root);
    if (!state || !state.playing) {
        return;
    }
    pauseInternal(state, currentSeconds(state));
    report(state, 'paused');
}

export function seek(root, seconds) {
    const state = states.get(root);
    if (!state || state.duration === 0) {
        return;
    }
    const target = Math.min(Math.max(seconds, 0), state.duration);
    if (state.playing) {
        startSources(state, target);
    } else {
        state.offset = target;
    }
    setPlayhead(state.canvas, target);
    refresh(state);
}

/// Switches which stem is audible. Deliberately does NOT restart the sources, so the position is
/// preserved exactly and the change is inaudible apart from the crossfade.
export function setMode(root, mode) {
    const state = states.get(root);
    if (!state || !(mode in MODE_GAINS)) {
        return;
    }
    state.mode = mode;
    if (state.context) {
        applyGains(state, false);
    }
    refresh(state);
}

export function currentTime(root) {
    const state = states.get(root);
    return state ? currentSeconds(state) : 0;
}

export function dispose(root) {
    const state = states.get(root);
    if (!state) {
        return;
    }
    if (state.frame !== null) {
        cancelAnimationFrame(state.frame);
    }
    stopSources(state);
    states.delete(root);
    if (state.context) {
        state.context.close();
    }
}
