// Autocorrelation pitch detection for the hum recorder's live line and the take plot's offline
// pass. Signal processing only — deciding WHAT frequency is sounding is allowed here; deciding
// what it MEANS musically stays server-side.

const MIN_FREQ = 60;
const MAX_FREQ = 1000;
const RMS_GATE = 0.01;
const CLARITY_GATE = 0.5;

/// Normalized-autocorrelation pitch detection over one mono frame. Returns a fractional MIDI
/// number, or null for silence and unpitched input.
export function detectPitch(samples, sampleRate) {
    let energy = 0;
    for (let i = 0; i < samples.length; i++) {
        energy += samples[i] * samples[i];
    }
    if (Math.sqrt(energy / samples.length) < RMS_GATE) {
        return null;
    }

    const minLag = Math.floor(sampleRate / MAX_FREQ);
    const maxLag = Math.min(Math.floor(sampleRate / MIN_FREQ), samples.length - 2);
    if (maxLag <= minLag) {
        return null;
    }

    let bestLag = -1;
    let bestScore = 0;
    const scores = new Float32Array(maxLag + 1);
    for (let lag = minLag; lag <= maxLag; lag++) {
        let sum = 0;
        let lagEnergy = 0;
        const terms = samples.length - lag;
        for (let i = 0; i < terms; i++) {
            sum += samples[i] * samples[i + lag];
            lagEnergy += samples[i + lag] * samples[i + lag];
        }
        const score = lagEnergy > 0 ? sum / Math.sqrt(energy * lagEnergy) : 0;
        scores[lag] = score;
        if (score > bestScore) {
            bestScore = score;
            bestLag = lag;
        }
    }
    if (bestLag < 0 || bestScore < CLARITY_GATE) {
        return null;
    }

    // Parabolic refinement around the winning lag for sub-bin precision.
    let lag = bestLag;
    if (bestLag > minLag && bestLag < maxLag) {
        const left = scores[bestLag - 1];
        const mid = scores[bestLag];
        const right = scores[bestLag + 1];
        const denom = left - (2 * mid) + right;
        if (Math.abs(denom) > 1e-12) {
            lag += Math.min(Math.max(0.5 * (left - right) / denom, -1), 1);
        }
    }

    const freq = sampleRate / lag;
    return 69 + (12 * Math.log2(freq / 440));
}
