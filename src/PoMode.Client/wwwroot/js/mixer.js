// Web Audio stem mixer (spec §7): mix, vocals and instrumental played sample-synchronised through
// GainNodes, switched by 50 ms ramps so there are no pops and the position is never lost.
//
// This module also owns the transport clock. It drives the canvas playhead directly through
// canvas.js rather than round-tripping 60 times a second through Blazor, so playback costs no
// component renders. Blazor only hears about discrete events (loaded, mode changed, failed, and
// keyboard-driven play/pause so the button label can follow).

import { setPlayhead } from './canvas.js';

const states = new Map();

const STEMS = ['mix', 'vocals', 'instrumental'];

/// Which stem is audible in each mode. Everything keeps playing; only the gains change.
const MODE_GAINS = {
    full: { mix: 1, vocals: 0, instrumental: 0 },
    vocals: { mix: 0, vocals: 1, instrumental: 0 },
    backing: { mix: 0, vocals: 0, instrumental: 1 },
};

/// The two synthesized note overlays. Each one toggles independently of the stem mode and of the
/// other overlay, so "Vocal Notes" and "Music Notes" only ever add or remove their own notes.
const NOTE_SOURCE_NAMES = ['vocal', 'backing'];

const RAMP_SECONDS = 0.05;

/// How far ahead of the playhead synth notes are committed to the audio clock. Generous enough to
/// survive a laggy tab (requestAnimationFrame pauses), short enough that a seek barely overlaps.
const SYNTH_LOOKAHEAD_SECONDS = 0.25;

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

/// Stops scheduled voices. With a source ('vocal', 'backing', 'click') only that layer's voices
/// are silenced; without one, everything is — pause and seek use the latter.
function stopSynthVoices(state, source) {
    const kept = [];
    for (const osc of state.synthVoices) {
        if (source !== undefined && osc.pmSource !== source) {
            kept.push(osc);
            continue;
        }
        try {
            osc.stop();
        } catch {
            // Already stopped.
        }
        osc.disconnect();
    }
    state.synthVoices = kept;
}

/// One synthesized note: a detuned oscillator pair through a lowpass and an ADSR envelope (the
/// timbre is borrowed from PoModeMm's midiPlayer.js). Vocal notes get a bright sawtooth lead;
/// backing notes get a mellower, quieter triangle so the melody stays in front when both play.
/// Envelope times are ordered even for very short notes.
function scheduleVoice(state, note, when, source) {
    const ctx = state.context;
    const freq = 440 * Math.pow(2, (note.midiPitch - 69) / 12);
    const duration = Math.max(0.05, note.durationSec);
    const level = source === 'backing' ? 0.5 : 1;
    const velocity = ((note.velocity ?? 90) / 127) * level;

    const osc1 = ctx.createOscillator();
    const osc2 = ctx.createOscillator();
    osc1.type = source === 'backing' ? 'triangle' : 'sawtooth';
    osc2.type = source === 'backing' ? 'triangle' : 'sawtooth';
    osc1.frequency.value = freq;
    osc2.frequency.value = freq * 1.001; // gentle detune for warmth

    const filter = ctx.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.value = source === 'backing' ? 2000 : 3200;
    filter.Q.value = 0.7;

    const env = ctx.createGain();
    env.gain.setValueAtTime(0.0001, when);
    env.gain.exponentialRampToValueAtTime(0.6 * velocity, when + 0.01);
    env.gain.exponentialRampToValueAtTime(0.35 * velocity, when + Math.min(0.15, Math.max(0.03, duration * 0.5)));
    env.gain.exponentialRampToValueAtTime(0.0001, when + duration + 0.05);

    osc1.connect(filter);
    osc2.connect(filter);
    filter.connect(env);
    env.connect(state.synthGain);

    for (const osc of [osc1, osc2]) {
        osc.pmSource = source;
        osc.start(when);
        osc.stop(when + duration + 0.1);
        osc.onended = () => {
            const index = state.synthVoices.indexOf(osc);
            if (index >= 0) {
                state.synthVoices.splice(index, 1);
            }
            osc.disconnect();
        };
        state.synthVoices.push(osc);
    }
}

/// Commits every note whose start falls between the cursor and the lookahead horizon to the audio
/// clock at its exact song position, then advances the cursor so nothing is scheduled twice.
function scheduleSynth(state) {
    const windowEnd = currentSeconds(state) + SYNTH_LOOKAHEAD_SECONDS;
    for (const source of NOTE_SOURCE_NAMES) {
        if (!state.noteSources[source]) {
            continue;
        }
        for (const note of state.notes[source]) {
            if (note.startSec >= state.synthCursor && note.startSec < windowEnd) {
                const when = state.startedAt + (note.startSec - state.offset);
                scheduleVoice(state, note, Math.max(when, state.context.currentTime), source);
            }
        }
    }
    state.synthCursor = windowEnd;
}

/// One metronome click: a short 1 kHz sine blip with a fast decay. Clicks are tagged like synth
/// voices so pause, seek and the metronome toggle can silence pending ones without touching the
/// note overlays.
function scheduleClick(state, when) {
    const ctx = state.context;
    const osc = ctx.createOscillator();
    osc.type = 'sine';
    osc.frequency.value = 1000;

    const env = ctx.createGain();
    env.gain.setValueAtTime(0.0001, when);
    env.gain.exponentialRampToValueAtTime(0.9, when + 0.002);
    env.gain.exponentialRampToValueAtTime(0.0001, when + 0.06);

    osc.connect(env);
    env.connect(state.clickGain);
    osc.pmSource = 'click';
    osc.start(when);
    osc.stop(when + 0.08);
    osc.onended = () => {
        const index = state.synthVoices.indexOf(osc);
        if (index >= 0) {
            state.synthVoices.splice(index, 1);
        }
        osc.disconnect();
    };
    state.synthVoices.push(osc);
}

/// Commits every beat between the click cursor and the lookahead horizon. The grid is regular
/// (beats.json: firstBeatSec + k·60/bpm — one tempo for the whole song), so beat times are
/// generated on the fly rather than looked up.
function scheduleClicks(state) {
    const grid = state.beatGrid;
    const windowEnd = currentSeconds(state) + SYNTH_LOOKAHEAD_SECONDS;
    const period = 60 / grid.bpm;
    const k = Math.max(0, Math.ceil(((state.clickCursor - grid.firstBeatSec) / period) - 1e-9));
    for (let t = grid.firstBeatSec + (k * period); t < windowEnd; t += period) {
        const when = state.startedAt + (t - state.offset);
        scheduleClick(state, Math.max(when, state.context.currentTime));
    }
    state.clickCursor = windowEnd;
}

/// AudioBufferSourceNodes are single-use, so every play and every seek builds a fresh set. They are
/// all started with the same `when`, which is what keeps the three stems sample-synchronised.
function startSources(state, offsetSeconds) {
    stopSources(state);
    stopSynthVoices(state);
    state.synthCursor = offsetSeconds;
    state.clickCursor = offsetSeconds;
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
        if (NOTE_SOURCE_NAMES.some(source => state.noteSources[source])) {
            scheduleSynth(state);
        }
        if (state.metronome && state.beatGrid) {
            scheduleClicks(state);
        }
    }
    state.frame = requestAnimationFrame(() => tick(state));
}

function pauseInternal(state, atSeconds) {
    state.offset = atSeconds;
    state.playing = false;
    stopSources(state);
    stopSynthVoices(state);
}

async function playInternal(state) {
    if (state.duration === 0 || state.playing) {
        return;
    }
    // Called from a click or a key press, so the autoplay policy lets the context resume here.
    if (state.context.state === 'suspended') {
        await state.context.resume();
    }
    startSources(state, state.offset >= state.duration ? 0 : state.offset);
    state.playing = true;
    applyGains(state, false);
    report(state, 'playing');
}

function seekInternal(state, seconds) {
    if (state.duration === 0) {
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

/// Global transport keys: Space toggles play/pause, comma jumps back to the start. Skipped while
/// the user is typing. preventDefault on Space stops the page scrolling and stops a focused
/// button firing its own click on keyup, which would undo the toggle.
function onKeyDown(state, event) {
    const target = event.target;
    if (event.repeat
        || (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable))) {
        return;
    }
    if (event.code === 'Space') {
        event.preventDefault();
        togglePlayback(state);
    } else if (event.key === ',') {
        seekInternal(state, 0);
    }
}

async function togglePlayback(state) {
    if (state.duration === 0) {
        return;
    }
    if (state.playing) {
        pauseInternal(state, currentSeconds(state));
        report(state, 'paused');
    } else {
        await playInternal(state);
    }
    // Keeps the Blazor play button's label in sync; playback itself never waits on this.
    state.dotNet?.invokeMethodAsync('OnTransportKey', state.playing);
}

// ---- exports ----

export function init(root, canvas, dotNetRef) {
    dispose(root);
    const state = {
        root,
        canvas,
        dotNet: dotNetRef ?? null,
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
        notes: { vocal: [], backing: [] },
        noteSources: { vocal: false, backing: false },
        synthGain: null,
        synthVoices: [],
        synthCursor: 0,
        metronome: false,
        beatGrid: null,
        clickGain: null,
        clickCursor: 0,
    };
    state.onKeyDown = event => onKeyDown(state, event);
    document.addEventListener('keydown', state.onKeyDown);
    states.set(root, state);
    report(state, 'idle');
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

    if (!state.synthGain) {
        state.synthGain = state.context.createGain();
        state.synthGain.gain.value = 0.35; // master synth level, below the stems' full-scale audio
        state.synthGain.connect(state.context.destination);
    }
    if (!state.clickGain) {
        state.clickGain = state.context.createGain();
        state.clickGain.gain.value = 0.5; // clicks sit under the stems but stay audible over them
        state.clickGain.connect(state.context.destination);
    }

    state.duration = Math.max(0, ...Object.values(state.buffers).map(buffer => buffer.duration));
    state.offset = 0;
    state.playing = false;
    // A new job starts with every overlay off, matching the Blazor toggles' reset.
    state.noteSources = { vocal: false, backing: false };
    state.metronome = false;
    state.beatGrid = null;
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

/// Fetches the transcribed note lists for the notes modes: the vocal melody (notes.json) and the
/// backing transcription (notes-backing.json). A missing or unreadable artifact leaves its mode
/// silent rather than breaking the mixer. Returns [vocalCount, backingCount].
export async function loadNotes(root, urls) {
    const state = states.get(root);
    if (!state) {
        return [0, 0];
    }
    const fetchList = async url => {
        try {
            const response = await fetch(url);
            return response.ok ? await response.json() : [];
        } catch {
            return [];
        }
    };
    const [vocal, backing] = await Promise.all([fetchList(urls.vocal), fetchList(urls.backing)]);
    state.notes = { vocal, backing };
    return [vocal.length, backing.length];
}

export async function play(root) {
    const state = states.get(root);
    if (!state) {
        return;
    }
    await playInternal(state);
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
    if (!state) {
        return;
    }
    seekInternal(state, seconds);
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

/// Fetches the beat grid (beats.json). Returns the BPM when the grid is usable, 0 otherwise — a
/// missing artifact or a low-confidence estimate means "no usable beats", and the caller keeps
/// the metronome unavailable.
export async function loadBeats(root, url) {
    const state = states.get(root);
    if (!state) {
        return 0;
    }
    try {
        const response = await fetch(url);
        if (!response.ok) {
            return 0;
        }
        const grid = await response.json();
        if (!grid || !(grid.bpm > 0) || !(grid.confidence > 0)) {
            return 0;
        }
        state.beatGrid = grid;
        return grid.bpm;
    } catch {
        return 0;
    }
}

/// Shows or hides one note overlay ('vocal' or 'backing'). Only that overlay's voices are
/// touched; the stems, the other overlay and the metronome keep playing untouched.
export function setNoteSource(root, source, enabled) {
    const state = states.get(root);
    if (!state || !NOTE_SOURCE_NAMES.includes(source)) {
        return;
    }
    const anyBefore = NOTE_SOURCE_NAMES.some(name => state.noteSources[name]);
    state.noteSources[source] = enabled;
    if (!enabled) {
        stopSynthVoices(state, source);
    } else if (state.playing && !anyBefore) {
        // The shared cursor stalled while both overlays were off; restart it at the playhead.
        state.synthCursor = currentSeconds(state);
    }
}

/// Turns the metronome click on or off without touching anything else.
export function setMetronome(root, enabled) {
    const state = states.get(root);
    if (!state) {
        return;
    }
    state.metronome = enabled;
    if (!enabled) {
        stopSynthVoices(state, 'click');
    } else if (state.playing) {
        state.clickCursor = currentSeconds(state);
    }
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
    document.removeEventListener('keydown', state.onKeyDown);
    stopSources(state);
    stopSynthVoices(state);
    states.delete(root);
    if (state.context) {
        state.context.close();
    }
}
