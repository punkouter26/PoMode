// Web Audio stem mixer (spec §7): mix, vocals and instrumental played sample-synchronised through
// GainNodes, switched by 50 ms ramps so there are no pops and the position is never lost.
//
// This module also owns the transport clock. It drives the canvas playhead directly through
// canvas.js rather than round-tripping 60 times a second through Blazor, so playback costs no
// component renders. Blazor only hears about discrete events (loaded, mode changed, failed, and
// keyboard-driven play/pause so the button label can follow).

import { setPlayhead, addLivePitch, clearLivePitch } from './canvas.js';
import { detectPitch, openMicrophone } from './live-pitch.js';

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
    // A touch of stereo width, deterministic per pitch so a repeated note sits in the same place.
    if (ctx.createStereoPanner) {
        const pan = ctx.createStereoPanner();
        pan.pan.value = ((source === 'backing' ? -0.12 : 0.12) + (((note.midiPitch % 5) - 2) * 0.05));
        env.connect(pan);
        pan.connect(state.synthGain);
    } else {
        env.connect(state.synthGain);
    }

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
        const notes = state.notes[source];
        let index = state.synthIndex[source];
        while (index < notes.length && notes[index].startSec < state.synthCursor) {
            index++;
        }
        for (; index < notes.length; index++) {
            const note = notes[index];
            if (note.startSec >= windowEnd) {
                break;
            }
            const when = state.startedAt + (note.startSec - state.offset);
            scheduleVoice(state, note, Math.max(when, state.context.currentTime), source);
        }
        state.synthIndex[source] = index;
    }
    state.synthCursor = windowEnd;
}

/// One metronome click: a short 1 kHz sine blip with a fast decay. Clicks are tagged like synth
/// voices so pause, seek and the metronome toggle can silence pending ones without touching the
/// note overlays.
function scheduleClick(state, when, accent) {
    const ctx = state.context;
    const osc = ctx.createOscillator();
    osc.type = 'sine';
    osc.frequency.value = accent ? 1500 : 1000;

    const env = ctx.createGain();
    env.gain.setValueAtTime(0.0001, when);
    env.gain.exponentialRampToValueAtTime(accent ? 1.2 : 0.85, when + 0.002);
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
    let beatIndex = k;
    for (let t = grid.firstBeatSec + (k * period); t < windowEnd; t += period, beatIndex++) {
        const when = state.startedAt + (t - state.offset);
        // Accent every fourth beat — the same 4/4 lead-sheet approximation the exports use.
        scheduleClick(state, Math.max(when, state.context.currentTime), beatIndex % 4 === 0);
    }
    state.clickCursor = windowEnd;
}

/// The shared output bus: everything routes through one master gain with an analyser tap, so the
/// reactive background and the spectrum wall can read levels without touching the audio path.
function ensureMasterGraph(state) {
    if (state.master) {
        return;
    }
    const ctx = state.context;
    state.master = ctx.createGain();
    state.master.gain.value = 1;
    state.analyser = ctx.createAnalyser();
    state.analyser.fftSize = 256;
    state.analyser.smoothingTimeConstant = 0.8;
    state.master.connect(state.analyser);
    state.master.connect(ctx.destination);
    state.levelData = new Uint8Array(state.analyser.fftSize);
    state.freqData = new Uint8Array(state.analyser.frequencyBinCount);
}

/// Gentle tanh soft-clip: rounds off synth peaks into warmth instead of digital edge.
function createSoftClip(ctx) {
    const shaper = ctx.createWaveShaper();
    const curve = new Float32Array(1024);
    for (let i = 0; i < curve.length; i++) {
        const x = (i / (curve.length - 1)) * 2 - 1;
        curve[i] = Math.tanh(1.5 * x);
    }
    shaper.curve = curve;
    shaper.oversample = '2x';
    return shaper;
}

/// A generated stereo impulse response (decaying noise) — a small room, no asset to ship.
function buildImpulse(ctx) {
    const seconds = 1.8;
    const length = Math.floor(ctx.sampleRate * seconds);
    const impulse = ctx.createBuffer(2, length, ctx.sampleRate);
    for (let channel = 0; channel < 2; channel++) {
        const data = impulse.getChannelData(channel);
        let seed = channel === 0 ? 1234567 : 7654321;
        for (let i = 0; i < length; i++) {
            // A tiny deterministic PRNG: identical reverb every load, nothing to cache.
            seed = (seed * 1103515245 + 12345) & 0x7fffffff;
            const noise = (seed / 0x3fffffff) - 1;
            data[i] = noise * Math.pow(1 - (i / length), 2.4);
        }
    }
    return impulse;
}

/// AudioBufferSourceNodes are single-use, so every play and every seek builds a fresh set. They are
/// all started with the same `when`, which is what keeps the three stems sample-synchronised.
function startSources(state, offsetSeconds) {
    stopSources(state);
    stopSynthVoices(state);
    state.synthCursor = offsetSeconds;
    state.synthIndex = { vocal: 0, backing: 0 };
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
    // A focused button/link must keep its own Space activation — hijacking it would make the
    // whole page keyboard-hostile. The shortcut still works from the page body and the canvas.
    if (target && typeof target.closest === 'function'
        && target.closest('button, [role="button"], a, select, [tabindex]')) {
        return;
    }
    if (event.code === 'Space') {
        event.preventDefault();
        togglePlayback(state);
    } else if (event.key === ',') {
        seekInternal(state, 0);
    } else if (event.key === 'm' || event.key === 'M') {
        // Stems mute toggle — independent of the source mode so synth-only listening is one key.
        const next = !state.stemsMuted;
        state.stemsMuted = next;
        applyStemMuting(state, next);
        state.root.dataset.mixerStemsMuted = next ? 'muted' : 'audible';
        state.dotNet?.invokeMethodAsync('OnStemsMutedChanged', next);
    } else if (event.key === '1') {
        setModeInternal(state, 'full');
        state.dotNet?.invokeMethodAsync('OnModeShortcut', 'full');
    } else if (event.key === '2') {
        setModeInternal(state, 'vocals');
        state.dotNet?.invokeMethodAsync('OnModeShortcut', 'vocals');
    } else if (event.key === '3') {
        setModeInternal(state, 'backing');
        state.dotNet?.invokeMethodAsync('OnModeShortcut', 'backing');
    } else if (event.key === 'v' || event.key === 'V') {
        const next = !state.noteSources.vocal;
        state.noteSources.vocal = next;
        if (!next) stopSynthVoices(state, 'vocal');
        state.dotNet?.invokeMethodAsync('OnSynthToggle', 'vocal', next);
    } else if (event.key === 'b' || event.key === 'B') {
        const next = !state.noteSources.backing;
        state.noteSources.backing = next;
        if (!next) stopSynthVoices(state, 'backing');
        state.dotNet?.invokeMethodAsync('OnSynthToggle', 'backing', next);
    } else if (event.key === 'k' || event.key === 'K') {
        state.dotNet?.invokeMethodAsync('OnKaraokeShortcut');
    }
}

function setModeInternal(state, mode) {
    state.mode = mode;
    if (state.context) {
        applyGains(state, false);
    }
    refresh(state);
}

/// Silences/un-silences the stem buses without touching the synth/drone/metronome buses — so
/// "Mute stems" + "Synth vocal" is the equivalent of "play only the MIDI".
function applyStemMuting(state, muted) {
    if (!state.context) {
        return;
    }
    const now = state.context.currentTime;
    for (const stem of STEMS) {
        const node = state.gains[stem];
        if (!node) {
            continue;
        }
        const mode = state.mode;
        const target = muted ? 0 : (MODE_GAINS[mode]?.[stem] ?? 0);
        node.gain.cancelScheduledValues(now);
        node.gain.setValueAtTime(node.gain.value, now);
        node.gain.linearRampToValueAtTime(target, now + RAMP_SECONDS);
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
        synthIndex: { vocal: 0, backing: 0 },
        stemsMuted: false,
        bpmNudge: 0,
        metronome: false,
        beatGrid: null,
        clickGain: null,
        clickCursor: 0,
        karaoke: null,
        master: null,
        analyser: null,
        levelData: null,
        freqData: null,
        synthShaper: null,
        reverb: null,
        drone: null,
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
    try {
        state.dotnet?.invokeMethodAsync('OnStemLoadProgress', 'Downloading audio stems (mix, vocals, backing)…');
    } catch { }

    let downloadedCount = 0;
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
            const buffer = await response.arrayBuffer();
            downloadedCount++;
            try {
                state.dotnet?.invokeMethodAsync('OnStemLoadProgress', `Decoding ${stem} stem (${downloadedCount}/3)…`);
            } catch { }
            return [stem, await state.context.decodeAudioData(buffer)];
        } catch {
            return [stem, null]; // an undecodable stem must not break the whole mixer
        }
    }));

    ensureMasterGraph(state);
    state.buffers = {};
    state.gains = {};
    for (const [stem, buffer] of loaded) {
        if (!buffer) {
            continue;
        }
        state.buffers[stem] = buffer;
        const gain = state.context.createGain();
        gain.gain.value = 0;
        gain.connect(state.master);
        state.gains[stem] = gain;
    }

    if (!state.synthGain) {
        const ctx = state.context;
        state.synthGain = ctx.createGain();
        state.synthGain.gain.value = 0.35; // master synth level, below the stems' full-scale audio
        // Synth bus: soft-clip warmth, then a dry path and a generated-impulse reverb send.
        state.synthShaper = createSoftClip(ctx);
        state.synthGain.connect(state.synthShaper);
        const dry = ctx.createGain();
        dry.gain.value = 0.85;
        state.synthShaper.connect(dry);
        dry.connect(state.master);
        state.reverb = ctx.createConvolver();
        state.reverb.buffer = buildImpulse(ctx);
        const wet = ctx.createGain();
        wet.gain.value = 0.25;
        state.synthShaper.connect(state.reverb);
        state.reverb.connect(wet);
        wet.connect(state.master);
    }
    if (!state.clickGain) {
        state.clickGain = state.context.createGain();
        state.clickGain.gain.value = 0.5; // clicks sit under the stems but stay audible over them
        state.clickGain.connect(state.master);
    }

    state.duration = Math.max(0, ...Object.values(state.buffers).map(buffer => buffer.duration));
    state.offset = 0;
    state.playing = false;
    // A new job starts with every overlay off, matching the Blazor toggles' reset.
    state.noteSources = { vocal: false, backing: false };
    state.metronome = false;
    state.beatGrid = null;
    stopKaraoke(root);
    stopDroneInternal(state);
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
    try {
        state.dotnet?.invokeMethodAsync('OnStemLoadProgress', 'Loading melody notes & beat grid…');
    } catch { }
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
    state.synthIndex = { vocal: 0, backing: 0 };
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
        state.synthIndex = { vocal: 0, backing: 0 };
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

/// Apply a +/- BPM offset to the metronome without touching any other layer. Used by the tap-tempo
/// + nudge buttons in the UI. Nudge of 0 restores the server BPM.
export function setBpmNudge(root, nudge) {
    const state = states.get(root);
    if (!state) {
        return;
    }
    state.bpmNudge = nudge;
    // The scheduling cursor must restart on the playhead because the period changed; pending
    // click voices on the old period are stranded either way and we silence them.
    if (state.playing) {
        state.clickCursor = currentSeconds(state);
        stopSynthVoices(state, 'click');
    }
}

/// Mute or unmute the stem audio without touching the synth/click/drone buses. So "Mute stems"
/// + "Synth vocal" is exactly "play only the MIDI".
export function setStemsMuted(root, muted) {
    const state = states.get(root);
    if (!state) {
        return;
    }
    state.stemsMuted = muted;
    applyStemMuting(state, muted);
    state.root.dataset.mixerStemsMuted = muted ? 'muted' : 'audible';
}

// ---- drone ----

function stopDroneInternal(state) {
    if (!state.drone) {
        return;
    }
    const now = state.context.currentTime;
    state.drone.gain.gain.cancelScheduledValues(now);
    state.drone.gain.gain.setValueAtTime(state.drone.gain.gain.value, now);
    state.drone.gain.gain.linearRampToValueAtTime(0.0001, now + 0.4);
    for (const osc of state.drone.oscillators) {
        osc.stop(now + 0.5);
    }
    state.drone = null;
    state.root.dataset.mixerDrone = 'off';
}

/// A soft tonic pad: two detuned triangles under a slow breathing LFO, through the synth bus so
/// it shares the reverb. The pitch itself was decided server-side (the detected tonic).
export async function setDrone(root, midiPitch) {
    const state = states.get(root);
    if (!state || !state.context) {
        return;
    }
    stopDroneInternal(state);
    if (midiPitch == null) {
        return;
    }
    const ctx = state.context;
    if (ctx.state === 'suspended') {
        await ctx.resume();
    }
    const freq = 440 * Math.pow(2, (midiPitch - 69) / 12);
    const gain = ctx.createGain();
    gain.gain.setValueAtTime(0.0001, ctx.currentTime);
    gain.gain.linearRampToValueAtTime(0.16, ctx.currentTime + 1.2);

    const lfo = ctx.createOscillator();
    lfo.frequency.value = 0.15;
    const lfoDepth = ctx.createGain();
    lfoDepth.gain.value = 0.04;
    lfo.connect(lfoDepth);
    lfoDepth.connect(gain.gain);

    const filter = ctx.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.value = 900;

    const oscillators = [lfo];
    for (const ratio of [1, 1.004]) {
        const osc = ctx.createOscillator();
        osc.type = 'triangle';
        osc.frequency.value = freq * ratio;
        osc.connect(filter);
        osc.start();
        oscillators.push(osc);
    }
    filter.connect(gain);
    gain.connect(state.synthGain);
    lfo.start();

    state.drone = { oscillators, gain };
    state.root.dataset.mixerDrone = 'on';
}

// ---- analyser taps (for the reactive background and the spectrum wall) ----

/// 0..1 RMS of whatever is currently audible, across all mixer instances (normally one).
export function masterLevel() {
    let level = 0;
    for (const state of states.values()) {
        if (!state.analyser || !(state.playing || state.drone)) {
            continue;
        }
        state.analyser.getByteTimeDomainData(state.levelData);
        let sum = 0;
        for (let i = 0; i < state.levelData.length; i++) {
            const v = (state.levelData[i] - 128) / 128;
            sum += v * v;
        }
        level = Math.max(level, Math.sqrt(sum / state.levelData.length));
    }
    return level;
}

/// The active mixer's byte frequency bins, or null when nothing is playing.
export function frequencyData() {
    for (const state of states.values()) {
        if (state.analyser && (state.playing || state.drone)) {
            state.analyser.getByteFrequencyData(state.freqData);
            return state.freqData;
        }
    }
    return null;
}

// ---- karaoke ----

/// The target note sounding at `seconds`, if any. Vocal notes are start-sorted; a few notes back
/// from the insertion point is enough because melody notes barely overlap.
function targetNoteAt(notes, seconds) {
    let low = 0;
    let high = notes.length;
    while (low < high) {
        const mid = (low + high) >> 1;
        if (notes[mid].startSec <= seconds) {
            low = mid + 1;
        } else {
            high = mid;
        }
    }
    for (let index = low - 1; index >= 0 && index >= low - 4; index--) {
        const note = notes[index];
        if (seconds >= note.startSec && seconds < note.startSec + note.durationSec) {
            return note;
        }
    }
    return null;
}

/// Octave-forgiving distance in semitones between a sung pitch and a target — singers routinely
/// land the right degree an octave away from the transcription.
function pitchClassDistance(sungMidi, targetMidi) {
    const diff = Math.abs(sungMidi - targetMidi) % 12;
    return Math.min(diff, 12 - diff);
}

function onKaraokeFrame(state, samples, sampleRate) {
    const karaoke = state.karaoke;
    if (!karaoke || !state.playing) {
        return;
    }
    const seconds = currentSeconds(state);
    const midi = detectPitch(samples, sampleRate);
    if (midi !== null) {
        addLivePitch(state.canvas, seconds, midi);
    }

    const target = targetNoteAt(state.notes.vocal, seconds);
    if (target) {
        karaoke.total++;
        if (midi !== null && pitchClassDistance(midi, target.midiPitch) <= 0.75) {
            karaoke.hits++;
        }
    }
    state.root.dataset.karaokeScore = String(karaokeScore(karaoke));
}

function karaokeScore(karaoke) {
    return karaoke.total === 0 ? 0 : Math.round((karaoke.hits / karaoke.total) * 100);
}

/// Karaoke mode: opens the microphone, traces the sung pitch onto the canvas, and scores it
/// against the vocal transcription. The caller is expected to mute the vocal stem itself
/// (setMode 'backing'). Returns false when the mic was denied or unavailable.
export async function startKaraoke(root) {
    const state = states.get(root);
    if (!state || !state.context || state.karaoke) {
        return state?.karaoke != null;
    }
    try {
        const karaoke = { hits: 0, total: 0, mic: null };
        karaoke.mic = await openMicrophone(
            (samples, sampleRate) => onKaraokeFrame(state, samples, sampleRate),
            state.context);
        state.karaoke = karaoke;
        clearLivePitch(state.canvas);
        state.root.dataset.karaoke = 'on';
        state.root.dataset.karaokeScore = '0';
        return true;
    } catch {
        state.root.dataset.karaoke = 'denied';
        return false;
    }
}

export function stopKaraoke(root) {
    const state = states.get(root);
    if (!state || !state.karaoke) {
        return;
    }
    state.karaoke.mic?.stop();
    state.karaoke = null;
    clearLivePitch(state.canvas);
    state.root.dataset.karaoke = 'off';
}

export function karaokeScoreOf(root) {
    const state = states.get(root);
    return state?.karaoke ? karaokeScore(state.karaoke) : 0;
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
    stopKaraoke(root);
    stopDroneInternal(state);
    stopSources(state);
    stopSynthVoices(state);
    states.delete(root);
    if (state.context) {
        state.context.close();
    }
}
