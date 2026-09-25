// Synth voices shared by every part of the app that plays notes it was handed: the mixer's note
// overlays, the voice map's "hear it" key, the Mode Lab's home-note bodies and the UI sounds.
//
// Before this each of those built its own oscillator pair, and the mixer's lead was a raw detuned
// sawtooth. That matters more here than in most apps: the note overlays are how a user *hears* what
// the analysis transcribed and which chord it named, so a voice that sounds like a test tone makes
// the transcription sound wrong even where it is right. Three voices, each chosen for what it has to
// carry:
//
//   lead    the sung melody: a rounded tone with a vibrato that arrives late, as a singer's does
//   pluck   the backing line: Karplus-Strong string, rendered once per pitch and cached
//   epiano  chords and single reference notes: two-operator FM, a bell-bright attack that settles
//
// Plus one generated room, so none of it plays in a dry vacuum. Every sample here is computed; there
// are no assets to fetch, which suits a network-first service worker.
//
// Like the rest of the player, this module knows no music theory. It is given MIDI numbers, start
// times and durations, and turns them into sound.

/// Per-context caches: a rendered string per pitch, one room per context. WeakMaps so a closed
/// context (a disposed mixer) takes its buffers with it.
const plucks = new WeakMap();
const rooms = new WeakMap();

function freqOf(midi) {
    return 440 * Math.pow(2, (midi - 69) / 12);
}

/// A small deterministic PRNG, so every load renders the same string and the same room.
function noise(seed) {
    let state = seed >>> 0;
    return () => {
        state = (state * 1103515245 + 12345) & 0x7fffffff;
        return (state / 0x3fffffff) - 1;
    };
}

/// A generated stereo room: decaying noise with a slightly different tail per ear, so the reverb has
/// width without a second asset. Cached per context — building 1.8 s of impulse on every play would
/// cost a visible frame.
export function room(ctx) {
    let convolver = rooms.get(ctx);
    if (convolver) {
        return convolver;
    }
    const seconds = 1.8;
    const length = Math.floor(ctx.sampleRate * seconds);
    const impulse = ctx.createBuffer(2, length, ctx.sampleRate);
    for (let channel = 0; channel < 2; channel++) {
        const data = impulse.getChannelData(channel);
        const next = noise(channel === 0 ? 1234567 : 7654321);
        for (let i = 0; i < length; i++) {
            data[i] = next() * Math.pow(1 - (i / length), 2.4);
        }
    }
    convolver = ctx.createConvolver();
    convolver.buffer = impulse;
    rooms.set(ctx, convolver);
    return convolver;
}

/// A dry/wet bus ending at `destination`: voices connect to the returned input. `wet` is the
/// reverb's share; the room itself is shared, so any number of buses cost one convolution.
export function bus(ctx, destination, wet = 0.22) {
    const input = ctx.createGain();
    const dry = ctx.createGain();
    dry.gain.value = 1 - (wet * 0.5);
    input.connect(dry);
    dry.connect(destination);
    const send = ctx.createGain();
    send.gain.value = wet;
    input.connect(send);
    const convolver = room(ctx);
    send.connect(convolver);
    // The shared room feeds every destination it has been bused to. Connecting twice is harmless in
    // Web Audio — a second connect between the same two nodes is ignored.
    convolver.connect(destination);
    return input;
}

/// Tracks the scheduled nodes of one note so the caller can stop it early (pause, seek) and so the
/// node graph is released when it ends on its own.
function finish(nodes, tail, onEnded) {
    const last = nodes[0];
    last.onended = () => {
        for (const node of tail) {
            try {
                node.disconnect();
            } catch {
                // Already disconnected by an early stop.
            }
        }
        onEnded?.(nodes);
    };
    return nodes;
}

/// The melody voice: a triangle with a quiet sine an octave up, rounded by a lowpass, and a vibrato
/// that only arrives after a quarter second — a held sung note starts straight and then blooms, and
/// a short one never wobbles at all.
export function lead(ctx, out, midi, when, duration, level = 0.5, onEnded = null) {
    const freq = freqOf(midi);
    const length = Math.max(0.06, duration);

    const body = ctx.createOscillator();
    body.type = 'triangle';
    body.frequency.value = freq;
    const air = ctx.createOscillator();
    air.type = 'sine';
    air.frequency.value = freq * 2;
    const airGain = ctx.createGain();
    airGain.gain.value = 0.18;

    const vibrato = ctx.createOscillator();
    vibrato.frequency.value = 5.4;
    const depth = ctx.createGain();
    depth.gain.setValueAtTime(0, when);
    if (length > 0.3) {
        // Ramp to about 14 cents, expressed in Hz of the fundamental.
        depth.gain.setValueAtTime(0, when + 0.25);
        depth.gain.linearRampToValueAtTime(freq * 0.008, when + Math.min(length, 0.6));
    }
    vibrato.connect(depth);
    depth.connect(body.frequency);
    depth.connect(air.frequency);

    const filter = ctx.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.value = Math.min(freq * 5, 5200);
    filter.Q.value = 0.6;

    const env = ctx.createGain();
    env.gain.setValueAtTime(0.0001, when);
    env.gain.exponentialRampToValueAtTime(level, when + 0.025);
    env.gain.exponentialRampToValueAtTime(level * 0.8, when + Math.min(0.2, length * 0.6));
    env.gain.setValueAtTime(level * 0.8, when + Math.max(length - 0.03, 0.03));
    env.gain.exponentialRampToValueAtTime(0.0001, when + length + 0.08);

    body.connect(filter);
    air.connect(airGain);
    airGain.connect(filter);
    filter.connect(env);
    env.connect(out);

    const stopAt = when + length + 0.12;
    for (const osc of [body, air, vibrato]) {
        osc.start(when);
        osc.stop(stopAt);
    }
    return finish([body, air, vibrato], [env, filter, airGain, depth], onEnded);
}

/// One plucked string, rendered by Karplus-Strong into a buffer and cached per pitch. The delay line
/// is an integer number of samples, so the true pitch is corrected by playback rate rather than
/// left up to three cents off at the top of the keyboard.
function pluckBuffer(ctx, midi) {
    let cache = plucks.get(ctx);
    if (!cache) {
        cache = new Map();
        plucks.set(ctx, cache);
    }
    const cached = cache.get(midi);
    if (cached) {
        return cached;
    }
    const rate = ctx.sampleRate;
    const freq = freqOf(midi);
    const period = Math.max(2, Math.round(rate / freq));
    const length = Math.floor(rate * 2.2);
    const buffer = ctx.createBuffer(1, length, rate);
    const data = buffer.getChannelData(0);
    // Low strings ring longer, as on a real instrument; the loss factor keeps the top from ringing
    // on like a bell.
    const loss = midi < 48 ? 0.998 : midi < 72 ? 0.996 : 0.992;
    const next = noise(midi * 7919);
    // A softened excitation (noise run through a one-pole lowpass) gives a finger rather than a pick.
    let smooth = 0;
    for (let i = 0; i < period; i++) {
        smooth = (smooth * 0.55) + (next() * 0.45);
        data[i] = smooth;
    }
    for (let i = period; i < length; i++) {
        const previous = data[i - period];
        const before = i - period - 1 >= 0 ? data[i - period - 1] : 0;
        data[i] = loss * 0.5 * (previous + before);
    }
    // The averaging filter adds half a sample of delay, so the string sounds at rate/(period+0.5).
    const entry = { buffer, playbackRate: freq / (rate / (period + 0.5)) };
    cache.set(midi, entry);
    return entry;
}

export function pluck(ctx, out, midi, when, duration, level = 0.5, onEnded = null) {
    const { buffer, playbackRate } = pluckBuffer(ctx, midi);
    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.playbackRate.value = playbackRate;
    const length = Math.min(Math.max(0.08, duration), buffer.duration - 0.05);

    const env = ctx.createGain();
    env.gain.setValueAtTime(level, when);
    env.gain.setValueAtTime(level, when + Math.max(length - 0.04, 0.02));
    env.gain.exponentialRampToValueAtTime(0.0001, when + length + 0.08);

    source.connect(env);
    env.connect(out);
    source.start(when);
    source.stop(when + length + 0.12);
    return finish([source], [env], onEnded);
}

/// Two-operator FM electric piano. A modulator at the carrier's own frequency, its depth decaying
/// from bright to mellow, plus a brief high-ratio "tine" partial for the strike. The shape of the
/// index envelope is what makes it read as a keyboard rather than an organ.
export function epiano(ctx, out, midi, when, duration, level = 0.4, onEnded = null) {
    const freq = freqOf(midi);
    const length = Math.max(0.12, duration);

    const carrier = ctx.createOscillator();
    carrier.frequency.value = freq;
    const modulator = ctx.createOscillator();
    modulator.frequency.value = freq;
    const index = ctx.createGain();
    index.gain.setValueAtTime(freq * 1.6, when);
    index.gain.exponentialRampToValueAtTime(freq * 0.25, when + Math.min(0.9, length));
    modulator.connect(index);
    index.connect(carrier.frequency);

    const tine = ctx.createOscillator();
    tine.frequency.value = freq * 14;
    const tineGain = ctx.createGain();
    tineGain.gain.setValueAtTime(level * 0.12, when);
    tineGain.gain.exponentialRampToValueAtTime(0.0001, when + 0.08);
    tine.connect(tineGain);

    const env = ctx.createGain();
    env.gain.setValueAtTime(0.0001, when);
    env.gain.exponentialRampToValueAtTime(level, when + 0.004);
    env.gain.exponentialRampToValueAtTime(level * 0.5, when + Math.min(0.45, length * 0.7));
    env.gain.setValueAtTime(level * 0.5, when + Math.max(length - 0.04, 0.05));
    env.gain.exponentialRampToValueAtTime(0.0001, when + length + 0.25);

    carrier.connect(env);
    tineGain.connect(env);
    env.connect(out);

    const stopAt = when + length + 0.3;
    carrier.start(when);
    modulator.start(when);
    tine.start(when);
    carrier.stop(stopAt);
    modulator.stop(stopAt);
    tine.stop(when + 0.1);
    return finish([carrier, modulator, tine], [env, index, tineGain], onEnded);
}

/// The voice for each named part, so callers pick by role rather than by synthesis method.
export const VOICES = { lead, pluck, epiano };
