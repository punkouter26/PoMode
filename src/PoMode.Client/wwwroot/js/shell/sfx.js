// Synthesized UI sounds and haptics. Everything is generated with plain oscillators on a lazily
// created shared AudioContext, kept deliberately quiet (peak gain well under 0.2), and always
// fail-silent — a sound that cannot play (autoplay policy, no audio hardware, closed context) is
// simply skipped, never thrown.
//
// The sounds are *modal*. An app whose whole subject is which note a piece treats as home should not
// answer every click with the same fixed 2 kHz blip, so the analysis hands this module the pitches of
// the detected mode through `setPalette` and the earcons are voiced from that set. Until a song is
// analyzed the palette is null and the sounds fall back to their fixed, keyless forms — which is the
// honest default, not a degraded one: there is no key yet to be in.
//
// The theory stays where it belongs. This module is handed a list of MIDI numbers and plays them; it
// never derives a scale, never decides major against minor, and the one chord it builds (`fanfare`)
// is deliberately tonic-fifth-octave so it states no third. Same rule canvas.js follows for colour.
//
// Every sound and every buzz is gated on fx-prefs: a user who has muted the effects hears nothing
// from here, and one who has turned the level off feels nothing either.

import * as prefs from './prefs.js';

let context = null;

/// Peak gain any single voice may reach. UI sounds should be felt, not heard over the music.
const PEAK = 0.12;

/// MIDI pitches of the detected mode, tonic first, as handed over by the analysis page. Null means
/// no song has been analyzed in this session yet.
let palette = null;

/// Records the mode's pitches so the earcons can be voiced in it. Called with an empty list or null
/// to go back to the keyless defaults — which is what leaving the analysis page should do, rather
/// than leaving the Library clicking in the key of whatever was open last.
export function setPalette(pitches) {
    palette = Array.isArray(pitches) && pitches.length > 0
        ? pitches.filter((p) => Number.isFinite(p))
        : null;
    if (palette && palette.length === 0) {
        palette = null;
    }
}

export function hasPalette() {
    return palette !== null;
}

function freqOf(midi) {
    return 440 * Math.pow(2, (midi - 69) / 12);
}

/// The palette pitch at `index`, wrapping upward by octaves past the top of the set so a run of
/// eight steps over a pentatonic keeps ascending instead of folding back on itself.
function paletteFreq(index) {
    if (!palette) {
        return null;
    }
    const span = palette.length;
    const octave = Math.floor(index / span);
    const pitch = palette[((index % span) + span) % span] + (12 * octave);
    return freqOf(pitch);
}

/// Returns a running AudioContext, or null when audio is unavailable, still blocked by the browser's
/// autoplay policy, or silenced by the user's effect settings. resume() is fired without awaiting: if
/// the call happened inside a user gesture the context comes alive in time; if not, this play is
/// skipped and a later one works.
function ensureContext() {
    try {
        if (!prefs.allowsSound()) {
            return null;
        }
        if (!context) {
            const Ctor = window.AudioContext ?? window.webkitAudioContext;
            if (!Ctor) {
                return null;
            }
            context = new Ctor();
        }
        if (context.state === 'suspended') {
            context.resume().catch(() => { });
        }
        return context.state === 'running' ? context : null;
    } catch {
        return null;
    }
}

/// One enveloped oscillator note: quick attack, exponential-ish release, self-disposing.
function voice(ctx, { type = 'sine', freq, when, duration, peak = PEAK, filterHz = null, detune = 0 }) {
    const osc = ctx.createOscillator();
    osc.type = type;
    osc.frequency.value = freq;
    if (detune !== 0) {
        osc.detune.value = detune;
    }

    const gain = ctx.createGain();
    gain.gain.setValueAtTime(0.0001, when);
    gain.gain.exponentialRampToValueAtTime(peak, when + Math.min(0.012, duration * 0.3));
    gain.gain.exponentialRampToValueAtTime(0.0001, when + duration);

    let head = osc;
    if (filterHz !== null) {
        const filter = ctx.createBiquadFilter();
        filter.type = 'bandpass';
        filter.frequency.value = filterHz;
        filter.Q.value = 4;
        head.connect(filter);
        head = filter;
    }
    head.connect(gain);
    gain.connect(ctx.destination);

    osc.start(when);
    osc.stop(when + duration + 0.05);
    osc.onended = () => gain.disconnect();
}

/// A 30 ms blip. Keyless it is a filtered 2 kHz switch click; with a palette it is the mode's tonic
/// two octaves up, so the interface ticks in the key of the song on screen.
export function click() {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const tonic = paletteFreq(0);
        if (tonic !== null) {
            voice(ctx, { type: 'triangle', freq: tonic * 4, when: ctx.currentTime, duration: 0.045, peak: 0.045 });
        } else {
            voice(ctx, { type: 'square', freq: 2000, when: ctx.currentTime, duration: 0.03, peak: 0.05, filterHz: 2000 });
        }
    } catch {
        // Fail silent.
    }
}

/// Two soft notes: keyless, 660 then 880 Hz; in key, the mode's tonic and its fifth degree.
export function chime() {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const now = ctx.currentTime;
        // Index 4 is the fifth degree of a seven-note mode and the fourth of a pentatonic — in both
        // cases a consonant step up from the tonic, which is all this needs to be.
        const low = paletteFreq(0) ?? 660;
        const high = paletteFreq(4) ?? 880;
        voice(ctx, { freq: low * (palette ? 2 : 1), when: now, duration: 0.22, peak: 0.09 });
        voice(ctx, { freq: high * (palette ? 2 : 1), when: now + 0.18, duration: 0.22, peak: 0.09 });
    } catch {
        // Fail silent.
    }
}

/// A soft filtered-noise sweep for transport starts: 0.18 s of "air" rising through a bandpass.
export function whoosh() {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const seconds = 0.18;
        const buffer = ctx.createBuffer(1, Math.floor(ctx.sampleRate * seconds), ctx.sampleRate);
        const data = buffer.getChannelData(0);
        let seed = 22222;
        for (let i = 0; i < data.length; i++) {
            seed = (seed * 1103515245 + 12345) & 0x7fffffff;
            data[i] = ((seed / 0x3fffffff) - 1) * (1 - (i / data.length));
        }
        const source = ctx.createBufferSource();
        source.buffer = buffer;
        const filter = ctx.createBiquadFilter();
        filter.type = 'bandpass';
        filter.Q.value = 1.2;
        const now = ctx.currentTime;
        filter.frequency.setValueAtTime(300, now);
        filter.frequency.exponentialRampToValueAtTime(2400, now + seconds);
        const gain = ctx.createGain();
        gain.gain.value = 0.07;
        source.connect(filter);
        filter.connect(gain);
        gain.connect(ctx.destination);
        source.start(now);
        source.onended = () => gain.disconnect();
    } catch {
        // Fail silent.
    }
}

/// New-personal-best celebration: a fast rising arpeggio blooming into a high shimmer. Louder than
/// the other UI sounds on purpose — it fires rarely and it is the reward. Climbs the mode's own
/// degrees when there is a palette, so a Phrygian best sounds like Phrygian.
export function cheer() {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const now = ctx.currentTime;
        const keyless = [523, 659, 784, 1046, 1318];
        for (let i = 0; i < 5; i++) {
            const freq = paletteFreq(i) !== null ? paletteFreq(i) * 2 : keyless[i];
            voice(ctx, { type: 'triangle', freq, when: now + (i * 0.07), duration: 0.3, peak: 0.11 });
        }
        for (let i = 0; i < 6; i++) {
            const shimmer = paletteFreq(i + 5) !== null ? paletteFreq(i + 5) * 4 : 2093 + (i * 392);
            voice(ctx, { type: 'sine', freq: shimmer, when: now + 0.35 + (i * 0.05), duration: 0.5, peak: 0.05 });
        }
    } catch {
        // Fail silent.
    }
}

/// A short ascending arpeggio for job completion: tonic, fifth, octave. Deliberately no third — the
/// client must not decide major versus minor; that call belongs to the server's analysis.
export function fanfare(tonicMidi) {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const midi = Number.isFinite(tonicMidi) ? tonicMidi : 60;
        const now = ctx.currentTime;
        const intervals = [0, 7, 12];
        for (let i = 0; i < intervals.length; i++) {
            voice(ctx, {
                type: 'triangle',
                freq: freqOf(midi + intervals[i]),
                when: now + i * 0.22,
                duration: 0.45,
                peak: 0.1,
            });
        }
    } catch {
        // Fail silent.
    }
}

/// One step of the analysis pipeline finishing. `index` counts completed stages from zero, so the
/// four stages of a run climb the mode's degrees and the job resolves upward instead of beeping the
/// same note four times. Quiet: this fires unattended while the user is doing something else.
export function stageStep(index) {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const step = Math.max(0, index | 0);
        // Degrees 0, 2, 4, 6 — every other note of the mode, which ascends fast enough that four
        // stages span most of an octave without needing to know how many there will be.
        const freq = paletteFreq(step * 2) ?? (440 * Math.pow(2, (step * 2) / 12));
        const now = ctx.currentTime;
        voice(ctx, { type: 'sine', freq: freq * 2, when: now, duration: 0.16, peak: 0.055 });
        voice(ctx, { type: 'sine', freq: freq * 4, when: now + 0.01, duration: 0.1, peak: 0.02 });
    } catch {
        // Fail silent.
    }
}

/// Practice: the singer landed on a target note. `strength` 0..1 scales brightness and level, so a
/// note just inside tolerance sounds duller than a dead-centre one without ever sounding wrong —
/// the verdict is the server's, this is only a nudge.
export function land(strength = 1) {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const s = Math.max(0, Math.min(1, Number(strength) || 0));
        const now = ctx.currentTime;
        voice(ctx, { type: 'sine', freq: 1200 + (s * 400), when: now, duration: 0.07, peak: 0.03 + (s * 0.03) });
    } catch {
        // Fail silent.
    }
}

/// Practice: a note outside the mode. A short flat-ish double tone rather than a buzzer — it marks
/// the moment without punishing it, and it is the same sound whatever the mode, because *which*
/// wrong note it was is the grader's finding to report in words.
export function outside() {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const now = ctx.currentTime;
        voice(ctx, { type: 'sine', freq: 320, when: now, duration: 0.1, peak: 0.035 });
        voice(ctx, { type: 'sine', freq: 320, when: now, duration: 0.1, peak: 0.03, detune: -38 });
    } catch {
        // Fail silent.
    }
}

/// Hum review: the sung note is a tone of the chord underneath it. Deliberately near-subliminal —
/// it plays over music, repeatedly, and anything louder would become the thing you listen to.
export function consonance() {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const now = ctx.currentTime;
        voice(ctx, { type: 'sine', freq: 2400, when: now, duration: 0.09, peak: 0.016 });
    } catch {
        // Fail silent.
    }
}

// ---- Haptics ----
//
// Worth having because this is an installable PWA with an Android share target, so a real share of
// its use is one-handed on a phone where a buzz lands better than a 60 ms sine. Gated on the motion
// level rather than the mute flag: muting is about not making noise in a room, and a silent phone in
// a pocket is exactly where the buzz is the point. Unsupported everywhere on iOS Safari, which is
// why nothing is ever gated *behind* it.

function vibrate(pattern) {
    try {
        if (!prefs.allowsMotion() || typeof navigator.vibrate !== 'function') {
            return;
        }
        navigator.vibrate(pattern);
    } catch {
        // Some browsers throw on a disallowed pattern; nothing here is load-bearing.
    }
}

/// A single short tick: a control changed, a take started.
export function tap() {
    vibrate(12);
}

/// A firmer confirmation: a recording stopped, a score arrived.
export function thud() {
    vibrate(28);
}

/// Two quick pulses for a celebration, matched to the confetti.
export function doubleTap() {
    vibrate([18, 60, 18]);
}
