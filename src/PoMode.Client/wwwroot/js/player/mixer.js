// Web Audio stem mixer (spec §7): mix, vocals and instrumental played sample-synchronised through
// GainNodes, switched by 50 ms ramps so there are no pops and the position is never lost.
//
// This module also owns the transport clock. It drives the canvas playhead directly through
// canvas.js rather than round-tripping 60 times a second through Blazor, so playback costs no
// component renders. Blazor only hears about discrete events (loaded, mode changed, failed, and
// keyboard-driven play/pause so the button label can follow).

import {
    setPlayhead, setOverlay as canvasSetOverlay,
    setWaveform, dropNotes, resetFx,
} from './canvas.js';
import { VOICES, bus } from './voices.js';
import { tap } from '../shell/sfx.js';

const states = new Map();

const STEMS = ['mix', 'vocals', 'instrumental'];

/// Which stem is audible in each mode. Everything keeps playing; only the gains change.
const MODE_GAINS = {
    full: { mix: 1, vocals: 0, instrumental: 0 },
    vocals: { mix: 0, vocals: 1, instrumental: 0 },
    backing: { mix: 0, vocals: 0, instrumental: 1 },
};

/// The synthesized note overlays. Each one toggles independently of the stem mode and of the
/// others, so vocal, backing and chord layers only ever add or remove their own notes.
const NOTE_SOURCE_NAMES = ['vocal', 'backing', 'chords'];

/// Per-source voice, same table idiom as MODE_GAINS: the sung melody gets the vocal-like lead,
/// centred slightly right; the transcribed backing a plucked string, left; the server-voiced chord
/// pad an electric piano in the middle, quietest so a sustained triad sits under everything. A new
/// source gets an explicit row here, never a silent fallthrough.
const VOICE_TIMBRES = {
    vocal: { voice: 'lead', level: 0.55, pan: 0.12 },
    backing: { voice: 'pluck', level: 0.5, pan: -0.12 },
    chords: { voice: 'epiano', level: 0.28, pan: 0 },
};

/// Stems whose level the canvas reacts to, mapped to the name the canvas knows them by. The mix is
/// not here: it is both at once, and the point is to tell them apart.
const METERED_STEMS = { vocals: 'vocal', instrumental: 'backing' };

/// One `{ vocal: v, backing: v, chords: v }` object per call, derived from NOTE_SOURCE_NAMES so
/// adding a source is a one-line change, not a hunt for every reset literal.
const perSource = value => Object.fromEntries(
    NOTE_SOURCE_NAMES.map(name => [name, typeof value === 'function' ? value() : value]));

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

/// One synthesized note in its source's voice (see voices.js), panned a touch per pitch so a
/// repeated note sits in the same place. Every scheduled node is tagged with its source so pause,
/// seek and a layer's toggle can silence it, and drops out of the list when it ends on its own.
function scheduleVoice(state, note, when, source) {
    const ctx = state.context;
    const timbre = VOICE_TIMBRES[source];
    const velocity = ((note.velocity ?? 90) / 127) * timbre.level;

    let out = state.synthGain;
    let pan = null;
    if (ctx.createStereoPanner) {
        pan = ctx.createStereoPanner();
        pan.pan.value = timbre.pan + (((note.midiPitch % 5) - 2) * 0.05);
        pan.connect(state.synthGain);
        out = pan;
    }
    const nodes = VOICES[timbre.voice](ctx, out, note.midiPitch, when, note.durationSec, velocity, ended => {
        state.synthVoices = state.synthVoices.filter(node => !ended.includes(node));
        pan?.disconnect();
    });
    for (const node of nodes) {
        node.pmSource = source;
        state.synthVoices.push(node);
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

/// Expands the per-measure tempo map into absolute beat times, once, when the map arrives.
///
/// A regular grid cannot click in time with a performance that drifts — by the end of a song that
/// slows from 88 to 54 BPM a fixed click is a bar out. Each measure contributes its own four beats
/// at its own measured tempo, starting from its own measured downbeat, so the clicks track the
/// playing rather than an average of it.
function buildTempoBeats(map, durationSec) {
    const beats = [];
    for (let i = 0; i < map.measures.length; i++) {
        const measure = map.measures[i];
        if (!(measure.bpm > 0)) {
            continue;
        }
        const period = 60 / measure.bpm;
        const end = i + 1 < map.measures.length ? map.measures[i + 1].startSec : durationSec;
        for (let beat = 0; beat < 4; beat++) {
            const at = measure.startSec + (beat * period);
            // A measure measured slightly long would otherwise spill a click past the next
            // downbeat, doubling it up against that measure's own first beat.
            if (at >= end - 1e-6 && beat > 0) {
                break;
            }
            beats.push({ at, accent: beat === 0 });
        }
    }
    return beats;
}

/// Commits every beat between the click cursor and the lookahead horizon.
///
/// Two sources, in order of preference: the per-measure tempo map when the song has one (clicks
/// follow the performance), else the regular grid from beats.json (one tempo, generated on the fly).
function scheduleClicks(state) {
    const windowEnd = currentSeconds(state) + SYNTH_LOOKAHEAD_SECONDS;

    if (state.tempoBeats) {
        // The cursor only moves forward, so the scan resumes where it stopped rather than
        // re-walking the whole song each callback.
        let index = state.tempoBeatIndex;
        while (index < state.tempoBeats.length && state.tempoBeats[index].at < state.clickCursor) {
            index++;
        }
        while (index < state.tempoBeats.length && state.tempoBeats[index].at < windowEnd) {
            const beat = state.tempoBeats[index];
            const when = state.startedAt + (beat.at - state.offset);
            scheduleClick(state, Math.max(when, state.context.currentTime), beat.accent);
            index++;
        }
        state.tempoBeatIndex = index;
        state.clickCursor = windowEnd;
        return;
    }

    const grid = state.beatGrid;
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

/// The tempo in force at a moment, from the measure covering it. Null when there is no map.
function tempoAt(state, seconds) {
    const map = state.tempoMap;
    if (!map) {
        return null;
    }
    let bpm = map.measures.length > 0 ? map.measures[0].bpm : null;
    for (const measure of map.measures) {
        if (measure.startSec > seconds) {
            break;
        }
        bpm = measure.bpm;
    }
    return bpm;
}

/// Reports the tempo to Blazor when — and only when — the measure the playhead is in changes it.
/// Called every frame, so it must stay a comparison: pushing a render per frame is exactly what
/// this module exists to avoid.
function reportTempo(state) {
    const bpm = tempoAt(state, currentSeconds(state));
    if (bpm === null || bpm === state.reportedBpm) {
        return;
    }
    state.reportedBpm = bpm;
    state.root.dataset.mixerBpm = bpm.toFixed(1);
    state.dotNet?.invokeMethodAsync('OnTempoChanged', bpm);
}

/// The shared output bus: everything routes through one master gain with an analyser tap, so the
/// playhead trail can read the overall level without touching the audio path.
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

/// AudioBufferSourceNodes are single-use, so every play and every seek builds a fresh set. They are
/// all started with the same `when`, which is what keeps the three stems sample-synchronised.
function startSources(state, offsetSeconds) {
    stopSources(state);
    stopSynthVoices(state);
    state.synthCursor = offsetSeconds;
    state.synthIndex = perSource(0);
    state.clickCursor = offsetSeconds;
    // The beat scan only moves forward, so a seek has to send it back to the start; scheduleClicks
    // then skips ahead to the cursor. Without this a backwards seek silences the metronome.
    state.tempoBeatIndex = 0;
    state.reportedBpm = null;
    const when = state.context.currentTime + 0.02; // a beat of headroom so all three share a start
    for (const stem of STEMS) {
        const buffer = state.buffers[stem];
        if (!buffer) {
            continue;
        }
        const source = state.context.createBufferSource();
        source.buffer = buffer;
        source.connect(state.gains[stem]);
        // Metered before the stem's gain, so the canvas hears what each stem is doing even while
        // the mode has it silenced — the glow says where the voice is, not what is audible.
        if (state.stemMeters[stem]) {
            source.connect(state.stemMeters[stem].analyser);
        }
        source.start(when, Math.min(offsetSeconds, buffer.duration));
        state.sources[stem] = source;
    }
    state.startedAt = when;
    state.offset = offsetSeconds;
}

/// 0..1 RMS of one analyser's current block. Cheap: one read of a 256-sample buffer.
function rms(analyser, data) {
    analyser.getByteTimeDomainData(data);
    let sum = 0;
    for (let i = 0; i < data.length; i++) {
        const v = (data[i] - 128) / 128;
        sum += v * v;
    }
    return Math.sqrt(sum / data.length);
}

/// 0..1 RMS of this mixer's own output, for the canvas playhead trail.
function levelOf(state) {
    return state.analyser ? rms(state.analyser, state.levelData) : 0;
}

/// Each metered stem's loudness, scaled so a sung phrase at a normal mix level lands near the top
/// of 0..1 — an RMS of 0.3 is already loud. Keyed by the canvas's names ('vocal', 'backing').
function stemLevelsOf(state) {
    const levels = {};
    for (const [stem, meter] of Object.entries(state.stemMeters)) {
        levels[METERED_STEMS[stem]] = Math.min(1, rms(meter.analyser, meter.data) * 3.2);
    }
    return levels;
}

/// A felt downbeat on a phone while the metronome is on: sfx.tap(), which already honours the
/// effects level. Downbeats come from the same two sources the clicks do, so the buzz lands on the
/// bar line the click accents. Only the stretch the playhead crossed since the last frame is looked at.
function hapticDownbeats(state, from, to) {
    if (!state.metronome || to <= from || to - from > 0.5) {
        return;
    }
    if (state.tempoBeats) {
        for (const beat of state.tempoBeats) {
            if (beat.at > to) {
                break;
            }
            if (beat.accent && beat.at > from) {
                tap();
                return;
            }
        }
        return;
    }
    const grid = state.beatGrid;
    if (grid && to >= grid.firstBeatSec) {
        const bar = 4 * 60 / grid.bpm;
        if (Math.floor((to - grid.firstBeatSec) / bar) > Math.floor((from - grid.firstBeatSec) / bar)) {
            tap();
        }
    }
}

function tick(state) {
    if (!states.has(state.root)) {
        return;
    }
    if (state.playing) {
        const seconds = currentSeconds(state);
        setPlayhead(state.canvas, seconds, levelOf(state), stemLevelsOf(state));
        hapticDownbeats(state, state.lastTickSeconds, seconds);
        state.lastTickSeconds = seconds;
        state.root.dataset.mixerTime = seconds.toFixed(3);
        if (seconds >= state.duration) {
            pauseInternal(state, state.duration);
            report(state, 'ended');
            dropNotes(state.canvas); // end-of-song easter egg; play/seek clears it
            return;
        }
        if (NOTE_SOURCE_NAMES.some(source => state.noteSources[source])) {
            scheduleSynth(state);
        }
        if (state.metronome && (state.tempoBeats || state.beatGrid)) {
            scheduleClicks(state);
        }
        reportTempo(state);
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
    resetFx(state.canvas); // any leftover end-of-song drop must vanish the moment audio restarts
    report(state, 'playing');
}

function seekInternal(state, seconds) {
    if (state.duration === 0) {
        return;
    }
    const target = Math.min(Math.max(seconds, 0), state.duration);
    state.lastTickSeconds = target; // a jump is not a crossing: no buzz for the bar lines skipped
    if (state.playing) {
        startSources(state, target);
    } else {
        state.offset = target;
    }
    resetFx(state.canvas);
    setPlayhead(state.canvas, target);
    refresh(state);
}

/// The whole keyboard surface: Space toggles play/pause, comma jumps back to the start. Every other
/// control is a visible button — deliberately, so there is nothing to memorise and nothing that can
/// silently disagree with what the buttons show. Skipped while the user is typing. preventDefault on
/// Space stops the page scrolling and stops a focused button firing its own click on keyup, which
/// would undo the toggle.
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
    }
}

function setModeInternal(state, mode) {
    state.mode = mode;
    if (state.context) {
        applyGains(state, false);
    }
    refresh(state);
}

/// Silences/un-silences the stem buses without touching the synth and metronome buses — so
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
        notes: perSource(() => []),
        noteSources: perSource(false),
        synthGain: null,
        synthVoices: [],
        synthCursor: 0,
        synthIndex: perSource(0),
        stemsMuted: false,
        metronome: false,
        beatGrid: null,
        tempoMap: null,
        tempoBeats: null,
        tempoBeatIndex: 0,
        reportedBpm: null,
        clickGain: null,
        clickCursor: 0,
        master: null,
        analyser: null,
        levelData: null,
        stemMeters: {},
        lastTickSeconds: 0,
        synthShaper: null,
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
        state.dotNet?.invokeMethodAsync('OnStemLoadProgress', 'Downloading audio stems (mix, vocals, backing)…');
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
                state.dotNet?.invokeMethodAsync('OnStemLoadProgress', `Decoding ${stem} stem (${downloadedCount}/3)…`);
            } catch { }
            return [stem, await state.context.decodeAudioData(buffer)];
        } catch {
            return [stem, null]; // an undecodable stem must not break the whole mixer
        }
    }));

    ensureMasterGraph(state);
    state.buffers = {};
    state.gains = {};
    state.stemMeters = {};
    for (const [stem, buffer] of loaded) {
        if (!buffer) {
            continue;
        }
        state.buffers[stem] = buffer;
        const gain = state.context.createGain();
        gain.gain.value = 0;
        gain.connect(state.master);
        state.gains[stem] = gain;
        if (stem in METERED_STEMS) {
            // A dead-end tap: an analyser measures what reaches it without being connected onward.
            const analyser = state.context.createAnalyser();
            analyser.fftSize = 256;
            state.stemMeters[stem] = { analyser, data: new Uint8Array(analyser.fftSize) };
        }
    }

    if (!state.synthGain) {
        const ctx = state.context;
        state.synthGain = ctx.createGain();
        state.synthGain.gain.value = 0.35; // master synth level, below the stems' full-scale audio
        // Synth bus: soft-clip warmth, then the shared generated room (voices.js) to the master.
        state.synthShaper = createSoftClip(ctx);
        state.synthGain.connect(state.synthShaper);
        state.synthShaper.connect(bus(ctx, state.master, 0.25));
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
    state.noteSources = perSource(false);
    state.metronome = false;
    state.beatGrid = null;
    state.tempoMap = null;
    state.tempoBeats = null;
    state.tempoBeatIndex = 0;
    state.reportedBpm = null;
    if (state.duration === 0) {
        report(state, 'unavailable');
        return 0;
    }

    applyGains(state, true);
    report(state, 'ready');

    // Hand the canvas the vocal waveform (the mix stands in when separation wrote no vocals):
    // ~1k min/max columns is plenty for a backdrop and cheap to extract once per load.
    const waveSource = state.buffers.vocals ?? state.buffers.mix;
    if (waveSource) {
        const channel = waveSource.getChannelData(0);
        const columns = 1024;
        const peaks = new Float32Array(columns * 2);
        const samplesPerColumn = Math.max(Math.floor(channel.length / columns), 1);
        for (let column = 0; column < columns; column++) {
            let low = 0;
            let high = 0;
            const start = column * samplesPerColumn;
            const end = Math.min(start + samplesPerColumn, channel.length);
            for (let i = start; i < end; i += 8) { // stride 8: a backdrop needs no exactness
                const value = channel[i];
                if (value < low) low = value;
                if (value > high) high = value;
            }
            peaks[column * 2] = low;
            peaks[(column * 2) + 1] = high;
        }
        setWaveform(state.canvas, peaks, waveSource.duration);
    }

    if (state.frame === null) {
        state.frame = requestAnimationFrame(() => tick(state));
    }
    return state.duration;
}

/// Fetches the note lists for the notes modes: the vocal melody (notes.json), the backing
/// transcription (notes-backing.json) and the server-voiced chord pad (notes-chords). A missing
/// or unreadable artifact leaves its mode silent rather than breaking the mixer.
/// Returns [vocalCount, backingCount, chordCount].
export async function loadNotes(root, urls) {
    const state = states.get(root);
    if (!state) {
        return [0, 0, 0];
    }
    try {
        state.dotNet?.invokeMethodAsync('OnStemLoadProgress', 'Loading melody notes & beat grid…');
    } catch { }
    const fetchList = async url => {
        try {
            const response = await fetch(url);
            return response.ok ? await response.json() : [];
        } catch {
            return [];
        }
    };
    const [vocal, backing, chords] = await Promise.all(
        [fetchList(urls.vocal), fetchList(urls.backing), fetchList(urls.chords)]);
    state.notes = { vocal, backing, chords };
    state.synthIndex = perSource(0);
    return [vocal.length, backing.length, chords.length];
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

/// Fetches the per-measure tempo map. Returns the median BPM when the map is usable, 0 otherwise.
///
/// Optional by design: a job analysed before the tempo map existed simply has no artifact, and the
/// metronome falls back to the regular beat grid. Only worth using with more than one measure —
/// a single measure is the flat grid with extra steps.
export async function loadTempoMap(root, url, durationSec) {
    const state = states.get(root);
    if (!state) {
        return 0;
    }
    try {
        const response = await fetch(url);
        if (!response.ok) {
            return 0;
        }
        const map = await response.json();
        if (!map || !Array.isArray(map.measures) || map.measures.length < 2 || !(map.confidence > 0)) {
            return 0;
        }
        state.tempoMap = map;
        state.tempoBeats = buildTempoBeats(map, durationSec > 0 ? durationSec : state.duration);
        state.tempoBeatIndex = 0;
        return map.medianBpm;
    } catch {
        // Same contract as loadBeats: an unreachable artifact costs the tempo track, never the mixer.
        return 0;
    }
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
        // The shared cursor stalled while all overlays were off; restart it at the playhead.
        state.synthCursor = currentSeconds(state);
        state.synthIndex = perSource(0);
    }
}

/// Forwards a canvas note-overlay toggle. Blazor holds the mixer module (and its root element),
/// not the canvas module, so the glow toggle routes through here to the canvas the mixer drives.
export function setOverlay(root, source, enabled) {
    const state = states.get(root);
    if (!state) {
        return;
    }
    canvasSetOverlay(state.canvas, source, enabled);
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

/// Mute or unmute the stem audio without touching the synth or click buses, so a solo layer plays
/// over silence rather than over the stems.
export function setStemsMuted(root, muted) {
    const state = states.get(root);
    if (!state) {
        return;
    }
    state.stemsMuted = muted;
    applyStemMuting(state, muted);
    state.root.dataset.mixerStemsMuted = muted ? 'muted' : 'audible';
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
