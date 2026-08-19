// Synthesized UI sounds: a click, a chime, and a completion fanfare. Everything is generated with
// plain oscillators on a lazily created shared AudioContext, kept deliberately quiet (peak gain
// well under 0.2), and always fail-silent — a sound that cannot play (autoplay policy, no audio
// hardware, closed context) is simply skipped, never thrown.

let context = null;

/// Peak gain any single voice may reach. UI sounds should be felt, not heard over the music.
const PEAK = 0.12;

/// Returns a running AudioContext, or null when audio is unavailable or still blocked by the
/// browser's autoplay policy. resume() is fired without awaiting: if the call happened inside a
/// user gesture the context comes alive in time; if not, this play is skipped and a later one works.
function ensureContext() {
    try {
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
function voice(ctx, { type = 'sine', freq, when, duration, peak = PEAK, filterHz = null }) {
    const osc = ctx.createOscillator();
    osc.type = type;
    osc.frequency.value = freq;

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

/// A 30 ms filtered blip around 2 kHz — the sound of a small mechanical switch, very quiet.
export function click() {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        voice(ctx, { type: 'square', freq: 2000, when: ctx.currentTime, duration: 0.03, peak: 0.05, filterHz: 2000 });
    } catch {
        // Fail silent.
    }
}

/// Two soft sine notes, 660 Hz then 880 Hz, about 0.4 s total.
export function chime() {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const now = ctx.currentTime;
        voice(ctx, { freq: 660, when: now, duration: 0.22, peak: 0.09 });
        voice(ctx, { freq: 880, when: now + 0.18, duration: 0.22, peak: 0.09 });
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

/// New-personal-best celebration: a fast rising arpeggio blooming into a high shimmer. Louder
/// than the other UI sounds on purpose — it fires rarely and it is the reward.
export function cheer() {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const now = ctx.currentTime;
        const riser = [523, 659, 784, 1046, 1318];
        for (let i = 0; i < riser.length; i++) {
            voice(ctx, { type: 'triangle', freq: riser[i], when: now + (i * 0.07), duration: 0.3, peak: 0.11 });
        }
        for (let i = 0; i < 6; i++) {
            voice(ctx, {
                type: 'sine',
                freq: 2093 + (i * 392),
                when: now + 0.35 + (i * 0.05),
                duration: 0.5,
                peak: 0.05,
            });
        }
    } catch {
        // Fail silent.
    }
}

/// A short ascending arpeggio for job completion: tonic, fifth, octave. Deliberately no third —
/// the client must not decide major versus minor; that call belongs to the server's analysis.
export function fanfare(tonicMidi) {
    const ctx = ensureContext();
    if (!ctx) {
        return;
    }
    try {
        const midi = Number.isFinite(tonicMidi) ? tonicMidi : 60;
        const freq = (m) => 440 * Math.pow(2, (m - 69) / 12);
        const now = ctx.currentTime;
        const intervals = [0, 7, 12];
        for (let i = 0; i < intervals.length; i++) {
            voice(ctx, {
                type: 'triangle',
                freq: freq(midi + intervals[i]),
                when: now + i * 0.22,
                duration: 0.45,
                peak: 0.1,
            });
        }
    } catch {
        // Fail silent.
    }
}
