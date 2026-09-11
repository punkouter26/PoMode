// "Does it sit?" — a sung take drawn against the chords it was sung over, live and then in full.
//
// The Mode Lab already makes the argument that hearing a take against its harmony is the only way to
// judge it, and reviewTake plays them together for exactly that reason. But the review was audio
// only, and the question it asks — did that phrase land on the harmony or fight it — is one the eye
// answers much faster than the ear on a first listen. So: the chord's notes as bands, the sung line
// over them, and the line lighting up where the two agree.
//
// Deliberately a 2D canvas. The app has three WebGL2 shaders and a WebGPU compute pass in it already,
// and each is there because the effect genuinely needs the GPU — tens of thousands of particles, a
// per-pixel field, a bloom that a stroke cannot fake. This is a few dozen filled rectangles and one
// polyline in a panel a few hundred pixels wide. Reaching for a fourth GL context here would cost a
// context, a shader compile and a fallback path to buy nothing a stroke and a shadow do not already
// give, and would push the page closer to the browser's context limit for no gain.
//
// What is and is not a judgment. The bands are the backing notes the SERVER voiced (ChordPadBuilder's
// counterpart in the Mode Lab, ModalMelodyGenerator) — this draws the pitches it was handed and never
// works out what chord they spell. "The sung note is in the chord" is a set membership test against
// those same server-supplied pitches, which is the same kind of lookup canvas.js does when it colours
// a note by the role VisualizationBuilder assigned. No mode is claimed for the voice anywhere here:
// that is the answer the analyzer exists to give, and the take is sent to it precisely so it can.

import { detectPitch } from './live-pitch.js';
import * as prefs from './fx-prefs.js';
import * as sfx from './sfx.js';

let state = null;

const MAX_DPR = 2;

/// The take is resampled to this rate before the pitch track is computed. Well above twice
/// live-pitch.js's 1000 Hz ceiling, and low enough that autocorrelating the whole take stays a
/// fraction of a second rather than the several it would take at 48 kHz.
const ANALYSIS_RATE = 16000;

/// Analysis window and hop, in samples at ANALYSIS_RATE. 1024 is long enough to hold two periods of
/// the lowest note the detector looks for; a 256-sample hop gives about 62 readings a second, which
/// is finer than any phrase needs and cheap at this rate.
const WINDOW = 1024;
const HOP = 256;

/// Semitones of padding above and below everything drawn, so nothing sits on an edge.
const PITCH_PADDING = 3;

/// Pitch classes count as agreeing when they match exactly. Octave-insensitive on purpose: singing
/// the third an octave up is still singing the third, and that is the question being asked.
function pitchClass(midi) {
    return ((Math.round(midi) % 12) + 12) % 12;
}

/// Averages an AudioBuffer's channels to mono at ANALYSIS_RATE with plain linear interpolation.
/// Good enough by a wide margin: the consumer is an autocorrelator looking for a fundamental, not
/// anything that would notice resampling artefacts up at Nyquist.
function toMono(buffer) {
    const ratio = buffer.sampleRate / ANALYSIS_RATE;
    const length = Math.floor(buffer.length / ratio);
    const out = new Float32Array(length);
    const channels = [];
    for (let c = 0; c < buffer.numberOfChannels; c++) {
        channels.push(buffer.getChannelData(c));
    }
    for (let i = 0; i < length; i++) {
        const position = i * ratio;
        const index = Math.floor(position);
        const frac = position - index;
        let sum = 0;
        for (const data of channels) {
            const a = data[index] ?? 0;
            const b = data[index + 1] ?? a;
            sum += a + ((b - a) * frac);
        }
        out[i] = sum / channels.length;
    }
    return out;
}

/// One pass over the take producing (timeSec, midi|null) readings. Runs once per take, on the main
/// thread, immediately after the decode the review already had to do — a second or two of audio
/// analyses in a few tens of milliseconds at this rate, which is well inside the gap between the
/// button press and the scheduled playback start.
function pitchTrack(mono) {
    const readings = [];
    for (let offset = 0; offset + WINDOW <= mono.length; offset += HOP) {
        const frame = mono.subarray(offset, offset + WINDOW);
        readings.push({
            timeSec: offset / ANALYSIS_RATE,
            midi: detectPitch(frame, ANALYSIS_RATE),
        });
    }
    return readings;
}

function resize(s) {
    const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);
    const width = Math.max(Math.round(s.canvas.clientWidth * dpr), 1);
    const height = Math.max(Math.round(s.canvas.clientHeight * dpr), 1);
    if (s.canvas.width !== width || s.canvas.height !== height) {
        s.canvas.width = width;
        s.canvas.height = height;
    }
    s.dpr = dpr;
}

function readColour(element, name, fallback) {
    const raw = getComputedStyle(element).getPropertyValue(name).trim();
    return raw || fallback;
}

function xFor(s, seconds) {
    return (seconds / Math.max(s.duration, 0.001)) * s.canvas.width;
}

function yFor(s, midi) {
    const span = Math.max(s.maxMidi - s.minMidi, 1);
    return s.canvas.height - (((midi - s.minMidi) / span) * s.canvas.height);
}

/// Normalises the server's backing notes and tiles them across `totalSeconds`.
///
/// Tiled because the progression loops under the take and HumTakeSeeder does exactly the same thing
/// server-side. Drawing one pass of the chords under a three-pass take would show the last two thirds
/// of the singing floating over nothing.
function tileBacking(s, backingNotes, loopDuration) {
    const backing = (backingNotes || [])
        .map((note) => ({
            midiPitch: note.midiPitch ?? note.MidiPitch,
            startSec: note.startSec ?? note.StartSec ?? 0,
            durationSec: note.durationSec ?? note.DurationSec ?? 0,
        }))
        .filter((note) => Number.isFinite(note.midiPitch));

    const loop = loopDuration > 0 ? loopDuration : s.duration;
    const tiled = [];
    for (let pass = 0; pass * loop < s.duration; pass++) {
        const offset = pass * loop;
        for (const note of backing) {
            if (offset + note.startSec >= s.duration) {
                continue;
            }
            tiled.push({ ...note, startSec: note.startSec + offset });
        }
    }
    s.backing = tiled;
}

/// The backing pitches sounding at `seconds`. Linear over the note list, which is fine: a Mode Lab
/// progression is a few dozen notes and this runs once per drawn frame.
function backingAt(s, seconds) {
    const sounding = [];
    for (const note of s.backing) {
        if (note.startSec <= seconds && seconds < note.startSec + note.durationSec) {
            sounding.push(note.midiPitch);
        }
    }
    return sounding;
}

function render(s, now) {
    s.raf = requestAnimationFrame((next) => render(s, next));
    resize(s);

    const ctx = s.ctx;
    const { width, height } = s.canvas;
    ctx.clearRect(0, 0, width, height);

    const playhead = s.clock ? s.clock() : null;

    // 1. The chord bands. One filled rectangle per backing note, spanning its own time and sitting
    //    at its own pitch — so the harmony is literally the ground the melody is drawn on.
    for (const note of s.backing) {
        const x = xFor(s, note.startSec);
        const w = Math.max(xFor(s, note.startSec + note.durationSec) - x, 1);
        const y = yFor(s, note.midiPitch);
        const bandHeight = Math.max(height / Math.max(s.maxMidi - s.minMidi, 1), 3);
        ctx.fillStyle = s.colours.band;
        ctx.globalAlpha = 0.5;
        ctx.fillRect(x, y - (bandHeight / 2), w, bandHeight);
    }
    ctx.globalAlpha = 1;

    // 2. The sung line, segmented by whether it agrees with the harmony under it. Drawn as separate
    //    strokes rather than one path with a changing colour, because a path cannot change stroke
    //    style mid-way and a gradient would blur the very distinction being made.
    ctx.lineWidth = Math.max(height * 0.018, 2);
    ctx.lineJoin = 'round';
    ctx.lineCap = 'round';

    let previous = null;
    for (const reading of s.readings) {
        if (reading.midi === null) {
            previous = null;
            continue;
        }
        if (previous !== null) {
            const agrees = reading.inChord;
            ctx.strokeStyle = agrees ? s.colours.consonant : s.colours.passing;
            // Only the agreeing segments glow; a passing note is not an error and should not be
            // lit up as one, but it should not be the thing the eye goes to either.
            ctx.shadowBlur = agrees ? Math.max(height * 0.05, 6) : 0;
            ctx.shadowColor = agrees ? s.colours.consonant : 'transparent';
            ctx.beginPath();
            ctx.moveTo(xFor(s, previous.timeSec), yFor(s, previous.midi));
            ctx.lineTo(xFor(s, reading.timeSec), yFor(s, reading.midi));
            ctx.stroke();
        }
        previous = reading;
    }
    ctx.shadowBlur = 0;

    // 3. The playhead, and the moment's agreement sounded as well as shown.
    if (playhead !== null && playhead >= 0 && playhead <= s.duration) {
        const x = xFor(s, playhead);
        ctx.strokeStyle = s.colours.playhead;
        ctx.lineWidth = Math.max(height * 0.012, 1.5);
        ctx.beginPath();
        ctx.moveTo(x, 0);
        ctx.lineTo(x, height);
        ctx.stroke();
        // Silent during the take itself. A tick on every chord tone as it is sung would be a live
        // judgment, and this page deliberately passes no judgment until the analyzer has: nudging a
        // singer toward the chords mid-phrase shapes the very melody the analysis is about to read.
        if (!s.live) {
            soundAgreement(s, playhead);
        }
    }
}

/// A near-subliminal tick as the playhead crosses into a note that agrees with the chord under it.
/// Fired on the crossing only, never while the note is held — this plays over music, and a sound
/// that repeated at the frame rate would become the only thing anyone heard.
function soundAgreement(s, playhead) {
    const index = Math.floor(playhead * (ANALYSIS_RATE / HOP));
    if (index === s.lastSounded || index < 0 || index >= s.readings.length) {
        return;
    }
    const previous = s.lastSounded;
    s.lastSounded = index;
    const reading = s.readings[index];
    const before = previous >= 0 && previous < s.readings.length ? s.readings[previous] : null;
    if (reading?.inChord && !before?.inChord) {
        sfx.consonance();
    }
}

/// Lays out the chord bands for a take that is about to be sung, so the singer sees the harmony
/// before they sing over it and their line fills in from the left as they do.
///
/// The difference from `prepare` is where the line comes from: there it is analysed out of a finished
/// recording, here it arrives a frame at a time from the microphone. Both draw the same picture, and
/// the live one is replaced by the analysed one as soon as the take is decoded — which is worth doing
/// rather than keeping the live trace, because the offline pass sees the whole take at a steadier hop
/// than the capture callback can manage.
///
/// `maxSeconds` bounds the time axis: a take runs until Stop, so unlike a review there is no known
/// duration to scale to.
export function prepareLive(backingNotes, loopDuration, maxSeconds) {
    if (!state) {
        return false;
    }
    state.duration = Math.max(maxSeconds > 0 ? maxSeconds : 60, 1);
    state.readings = [];
    state.live = true;
    state.lastSounded = -1;
    tileBacking(state, backingNotes, loopDuration);

    // Only the chords are known yet, so the drawn range is theirs; pushLive widens it if the singer
    // goes outside it, which is the one case where the plot must not clip the voice.
    let min = Infinity;
    let max = -Infinity;
    for (const note of state.backing) {
        min = Math.min(min, note.midiPitch);
        max = Math.max(max, note.midiPitch);
    }
    if (!Number.isFinite(min)) {
        min = 55;
        max = 79;
    }
    state.minMidi = min - PITCH_PADDING;
    state.maxMidi = max + PITCH_PADDING;

    // The playhead follows the microphone rather than an audio offset: during a take the take IS the
    // clock, and there is no decoded buffer to schedule against yet.
    state.clock = () => state.liveSeconds;
    state.liveSeconds = 0;
    if (state.raf === null) {
        state.raf = requestAnimationFrame((now) => render(state, now));
    }
    state.canvas.dataset.humReview = 'live';
    return true;
}

/// One heard pitch during a take, timed from the loop's downbeat. Called from hum-recorder.js's
/// capture callback, so it stays allocation-free in the common case and does no work beyond
/// appending a reading and deciding whether it agrees with the chord under it.
export function pushLive(seconds, midi) {
    if (!state || !state.live || seconds < 0) {
        return;
    }
    state.liveSeconds = seconds;
    if (seconds > state.duration) {
        return;
    }
    if (midi === null || !Number.isFinite(midi)) {
        // A gap is recorded rather than skipped, so the line breaks where the singer stopped instead
        // of drawing a straight segment across a rest.
        state.readings.push({ timeSec: seconds, midi: null, inChord: false });
        return;
    }
    // A voice outside the chords' own range must not be clipped off the plot — the take is the
    // subject here, the bands are the context.
    if (midi < state.minMidi + 1) {
        state.minMidi = midi - PITCH_PADDING;
    }
    if (midi > state.maxMidi - 1) {
        state.maxMidi = midi + PITCH_PADDING;
    }
    state.readings.push({
        timeSec: seconds,
        midi,
        inChord: backingAt(state, seconds).some((b) => pitchClass(b) === pitchClass(midi)),
    });
}

/// Starts the panel on `canvas`. Returns false with effects off or without a 2D context, in which
/// case the review is audio-only exactly as it was.
export function init(canvas) {
    dispose();
    if (!canvas || typeof canvas.getContext !== 'function' || !prefs.allowsMotion()) {
        return false;
    }
    const ctx = canvas.getContext('2d');
    if (!ctx) {
        return false;
    }
    state = {
        canvas,
        ctx,
        dpr: 1,
        readings: [],
        backing: [],
        duration: 1,
        // True while a take is being sung: the playhead follows the microphone and readings arrive
        // one at a time, rather than the whole track being known up front.
        live: false,
        liveSeconds: 0,
        minMidi: 55,
        maxMidi: 79,
        clock: null,
        lastSounded: -1,
        raf: null,
        unsubscribe: null,
        colours: {
            band: readColour(canvas, '--pm-chord-block', '#e4e4ed'),
            consonant: readColour(canvas, '--pm-note-chord-tone', '#2563eb'),
            passing: readColour(canvas, '--pm-fg-muted', '#77777f'),
            playhead: readColour(canvas, '--pm-playhead', '#e11d48'),
        },
    };
    resize(state);
    state.unsubscribe = prefs.subscribe(() => {
        if (!prefs.allowsMotion()) {
            dispose();
        }
    });
    canvas.dataset.humReview = 'ready';
    return true;
}

/// Analyses a decoded take against the backing it was sung over, and draws the result as a still.
///
/// Called by hum-recorder.js with the buffer it decoded for playback, so the decode is not done
/// twice. `backingNotes` are the server's own voiced chord notes, tiled across the take the same way
/// the seeder tiles them — this reads their pitches and never infers a chord from them.
export function prepare(audioBuffer, backingNotes, loopDuration) {
    if (!state || !audioBuffer) {
        return false;
    }
    state.duration = Math.max(audioBuffer.duration, 0.001);
    state.lastSounded = -1;
    // The live trace, if there was one, is replaced wholesale: the offline pass sees the take at a
    // steadier hop than a capture callback can manage, so keeping both would show two readings of
    // one performance.
    state.live = false;
    tileBacking(state, backingNotes, loopDuration);

    state.readings = pitchTrack(toMono(audioBuffer));

    // Agreement is decided once, here, rather than per frame: the sets it tests against do not
    // change while the take plays, and doing it in the draw loop would be the same work sixty times
    // a second for the same answer.
    for (const reading of state.readings) {
        reading.inChord = reading.midi !== null
            && backingAt(state, reading.timeSec).some((midi) => pitchClass(midi) === pitchClass(reading.midi));
    }

    // The drawn range covers the voice and the harmony together — the comparison is the point, so
    // clipping either to flatter the other would defeat it.
    let min = Infinity;
    let max = -Infinity;
    for (const note of state.backing) {
        min = Math.min(min, note.midiPitch);
        max = Math.max(max, note.midiPitch);
    }
    for (const reading of state.readings) {
        if (reading.midi !== null) {
            min = Math.min(min, reading.midi);
            max = Math.max(max, reading.midi);
        }
    }
    if (!Number.isFinite(min)) {
        min = 55;
        max = 79;
    }
    state.minMidi = min - PITCH_PADDING;
    state.maxMidi = max + PITCH_PADDING;

    // One frame now, so the panel shows the take before anyone presses play.
    state.clock = null;
    render(state, performance.now());
    cancelAnimationFrame(state.raf);
    state.raf = null;
    state.canvas.dataset.humReview = 'prepared';
    return true;
}

/// Starts the playhead. `clock` returns the current position in seconds, or null — hum-recorder.js
/// supplies one reading off the shared AudioContext, so the line and the audio cannot drift.
export function play(clock) {
    if (!state) {
        return;
    }
    state.clock = clock;
    state.lastSounded = -1;
    if (state.raf === null) {
        state.raf = requestAnimationFrame((now) => render(state, now));
    }
    state.canvas.dataset.humReview = 'playing';
}

/// Stops the playhead, leaving the take on screen as a still.
export function stop() {
    if (!state) {
        return;
    }
    state.clock = null;
    if (state.raf !== null) {
        cancelAnimationFrame(state.raf);
        state.raf = null;
    }
    render(state, performance.now());
    cancelAnimationFrame(state.raf);
    state.raf = null;
    state.canvas.dataset.humReview = 'prepared';
}

/// Forgets the take — a redo, or one that has been saved.
export function clear() {
    if (!state) {
        return;
    }
    stop();
    state.live = false;
    state.liveSeconds = 0;
    state.readings = [];
    state.backing = [];
    state.ctx.clearRect(0, 0, state.canvas.width, state.canvas.height);
    state.canvas.dataset.humReview = 'ready';
}

export function dispose() {
    if (!state) {
        return;
    }
    if (state.raf !== null) {
        cancelAnimationFrame(state.raf);
    }
    state.unsubscribe?.();
    try {
        delete state.canvas.dataset.humReview;
    } catch {
        // Already detached.
    }
    state = null;
}
