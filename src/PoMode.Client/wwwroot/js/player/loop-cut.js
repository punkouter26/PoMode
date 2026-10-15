// Splices a drum recording made without a click into a loop at a stated tempo.
//
// Pure arithmetic over samples, no audio graph and no DOM, so the same file runs under Node
// (tests/loop-cut.check.mjs). Nothing is stretched: the recording is cut at its hits, each hit is
// given the sixteenth-note it was played on, and the pieces are laid back down on that grid at the
// tempo asked for. A hand-played take drifts, so the grid follows the player from hit to hit rather
// than being ruled once across the whole recording.

const HOP = 128;                // envelope resolution: 2.7 ms at 48 kHz
const WINDOW = 512;             // level is read over ~10 ms, so one cycle of a low drum is not a "rise"
const RISE_HOPS = 4;            // a hit is the level now against the level ~10 ms ago
const RISE_RATIO = 1.3;         // and it has to be a real step up, not a wobble inside a decay
const ONSET_GAP_SEC = 0.05;     // two rises closer than this are one hit (a flam stays in one slice)
const ONSET_FLOOR = 0.15;       // ponytail: fixed share of the loudest rise, tuned on one phone recording; quieter ghost notes ride inside the slice before them. A sensitivity control if that proves too coarse
const PULSE_MIN_SEC = 0.06;      // a sixteenth at 250 BPM
const PULSE_MAX_SEC = 1.0;       // a beat at 60 BPM
const PULSE_HORIZON_SEC = 2.5;  // gaps longer than this say more about drift than about the pulse
const ATTACK_SHARE = 0.25;      // a slice starts where the hit first reaches this share of its own peak
const PRE_ROLL = 64;            // and a little ahead of that
const FADE_SEC = 0.003;         // every slice ends on a short fade, so a cut never clicks
const TAIL_SEC = 0.25;          // decay past the last bar, folded over the start of the loop
const LEAD_IN_GAP_SEC = 0.75;   // a first "hit" this far ahead of the next one, and quieter than the take's usual hit, is the mic opening or a bump before the playing starts
export const STEPS_PER_BAR = 16;    // ponytail: straight sixteenths in 4/4; swing and triplets get squared off. A grid choice if someone needs them

/// The lengths a loop may be: phrases come in 1, 2, 4 and 8 bars, and a loop of any other length
/// drifts against the chords as a polymeter that nobody asked for by recording three bars of drums.
export const LOOP_BARS = Object.freeze([1, 2, 4, 8]);

/// The loop length for a take of `played` bars: the longest allowed length that the take fills, so a
/// three-bar take loops its first two bars rather than padding a fourth with silence.
export function loopBars(played) {
    let bars = LOOP_BARS[0];
    for (const allowed of LOOP_BARS) {
        if (allowed <= played) bars = allowed;
    }
    return bars;
}

export function loopFrameCount(bpm, bars, sampleRate) {
    return Math.round(bars * 4 * 60 / bpm * sampleRate);
}

/// The hits in the order they were played, as { frame, weight }: local peaks of the rise in level,
/// each placed at the start of its own attack. Silence answers an empty list.
export function findOnsets(samples, sampleRate) {
    const hops = Math.max(0, Math.floor((samples.length - WINDOW) / HOP) + 1);
    const level = new Float32Array(hops);
    for (let h = 0; h < hops; h++) {
        let energy = 0;
        for (let i = h * HOP; i < h * HOP + WINDOW; i++) energy += samples[i] * samples[i];
        level[h] = Math.sqrt(energy / WINDOW);
    }
    const flux = new Float32Array(hops);
    let peak = 0;
    for (let h = RISE_HOPS; h < hops; h++) {
        const before = level[h - RISE_HOPS];
        flux[h] = level[h] > RISE_RATIO * before ? level[h] - before : 0;
        if (flux[h] > peak) peak = flux[h];
    }
    if (peak <= 0) return [];

    const radius = Math.round(ONSET_GAP_SEC * sampleRate / HOP);
    const onsets = [];
    for (let h = 0; h < hops; h++) {
        if (flux[h] <= 0 || flux[h] < ONSET_FLOOR * peak) continue;
        let isPeak = true;
        for (let n = Math.max(0, h - radius); n <= Math.min(hops - 1, h + radius) && isPeak; n++) {
            // Strict on one side only, so two equal neighbours yield one hit rather than none.
            isPeak = n < h ? flux[n] < flux[h] : flux[n] <= flux[h];
        }
        if (!isPeak) continue;

        // The envelope says a hit is in this neighbourhood; the samples say where it begins.
        const from = Math.max(0, h * HOP - WINDOW);
        const to = Math.min(samples.length, h * HOP + WINDOW);
        let loudest = 0;
        for (let i = from; i < to; i++) loudest = Math.max(loudest, Math.abs(samples[i]));
        let frame = from;
        while (frame < to && Math.abs(samples[frame]) < ATTACK_SHARE * loudest) frame++;
        onsets.push({ frame, weight: flux[h] });
    }
    return dropLeadIn(onsets, sampleRate);
}

/// The first hit is beat one, so it has to be the first thing the player played. A microphone take
/// can open on a pop or a bump of the desk, and taken as beat one that puts the silence between it
/// and the real first hit at the top of every pass. Such a sound is quieter than the take's typical hit and
/// stands alone ahead of the playing; a real first hit is neither, so a hit, a sample cut to start on
/// one, or a pickup straight into the groove is kept.
function dropLeadIn(onsets, sampleRate) {
    if (onsets.length < 2) return onsets;
    const weights = onsets.map((onset) => onset.weight).sort((a, b) => a - b);
    const typical = weights[weights.length >> 1];
    let first = 0;
    while (first < onsets.length - 1
        && onsets[first].weight < typical
        && onsets[first + 1].frame - onsets[first].frame > LEAD_IN_GAP_SEC * sampleRate) {
        first++;
    }
    return first === 0 ? onsets : onsets.slice(first);
}

/// The take's pulse, in BPM: the period that the gaps between nearby hits are most nearly whole
/// multiples of. Human timing blurs short periods more than long ones, so a subdivision of the real
/// pulse scores below it and its double (which half the gaps miss) far below. Scored on gaps, not on
/// distance from the first hit, so drift across the take does not blur it. Quadratic in hits and
/// independent of any target tempo, so a caller cutting one take to many tempos finds it once.
/// Null when there are too few hits to have a pulse.
export function pulseBpm(onsets, sampleRate) {
    if (onsets.length < 2) return null;
    const horizon = PULSE_HORIZON_SEC * sampleRate;
    let pulse = 0;
    let bestScore = -Infinity;
    for (let period = PULSE_MIN_SEC * sampleRate; period <= PULSE_MAX_SEC * sampleRate; period *= 1.0025) {
        let score = 0;
        for (let i = 0; i < onsets.length; i++) {
            for (let j = i + 1; j < onsets.length; j++) {
                const gap = onsets[j].frame - onsets[i].frame;
                if (gap > horizon) break;
                score += onsets[i].weight * onsets[j].weight * Math.cos(2 * Math.PI * gap / period);
            }
        }
        if (score > bestScore) {
            bestScore = score;
            pulse = period;
        }
    }

    return 60 * sampleRate / pulse;
}

/// The tempo the take was played at. A pulse is a beat, an eighth or a sixteenth, and nothing in the
/// audio says which; the reading taken is the one within half an octave of the target, which is the
/// one that moves the hits least.
function sourceTempo(pulse, targetBpm) {
    if (pulse === null) return targetBpm;
    let bpm = pulse;
    while (bpm > targetBpm * Math.SQRT2) bpm /= 2;
    while (bpm < targetBpm / Math.SQRT2) bpm *= 2;
    return bpm;
}

/// Gives every hit its sixteenth. The first hit is beat one; each later hit is counted on from the
/// one before by the gap between them, with the step length eased toward what the player is actually
/// doing, so a take that speeds up is followed instead of slowly sliding off a fixed grid.
/// `pulse` is `pulseBpm`'s answer for these onsets, passed in when the caller already has it.
/// Answers { slices: [{ frame, step }], sourceBpm, bars }.
export function layOut(onsets, sampleRate, targetBpm, pulse = pulseBpm(onsets, sampleRate)) {
    if (onsets.length === 0) return { slices: [], sourceBpm: targetBpm, bars: 0 };

    const sourceBpm = sourceTempo(pulse, targetBpm);
    const nominal = 15 / sourceBpm * sampleRate;
    let local = nominal;
    let previous = onsets[0].frame;
    let step = 0;
    const slices = [{ frame: Math.max(0, previous - PRE_ROLL), step: 0 }];
    for (let i = 1; i < onsets.length; i++) {
        const gap = onsets[i].frame - previous;
        const steps = Math.round(gap / local);
        if (steps < 1) continue;        // same sixteenth as the hit before: it stays in that slice
        step += steps;
        local = Math.min(1.15 * nominal, Math.max(0.85 * nominal, 0.75 * local + 0.25 * gap / steps));
        previous = onsets[i].frame;
        slices.push({ frame: Math.max(0, previous - PRE_ROLL), step });
    }
    // A last hit exactly on a bar line is the "one" the player landed on to finish, the start of the
    // next pass rather than a bar of its own, so it does not add a bar.
    return { slices, sourceBpm, bars: Math.max(1, Math.ceil(step / STEPS_PER_BAR)) };
}

/// The loop: exactly `bars` bars at `bpm`, each slice starting on its own sixteenth. A slice longer
/// than its slot (the target is faster than the take) is faded short; a shorter one leaves its own
/// decay and then silence. Whatever the last slice still rings past the final bar is faded and laid
/// over beat one, which is where that decay falls on every pass after the first.
export function buildLoop(samples, sampleRate, bpm, slices, bars) {
    const length = loopFrameCount(bpm, bars, sampleRate);
    const loop = new Float32Array(length);
    const stepFrames = 15 / bpm * sampleRate;
    const fade = Math.round(FADE_SEC * sampleRate);
    const tail = Math.min(length, Math.round(TAIL_SEC * sampleRate));
    // Steps only rise, so the kept slices are always a prefix of `slices`.
    const kept = slices.filter((slice) => slice.step < bars * STEPS_PER_BAR);

    for (let k = 0; k < kept.length; k++) {
        const from = kept[k].frame;
        // The source ends at the next hit in the recording, kept or not.
        const next = slices[k + 1];
        const available = (next ? next.frame : samples.length) - from;
        const at = Math.round(kept[k].step * stepFrames);
        const isLast = k === kept.length - 1;
        const slot = (isLast ? length : Math.round(kept[k + 1].step * stepFrames)) - at;
        const count = Math.min(available, isLast ? slot + tail : slot);
        const fadeFrames = isLast && count > slot ? count - slot : Math.min(fade, count);
        for (let i = 0; i < count; i++) {
            const gain = i >= count - fadeFrames ? (count - i) / fadeFrames : 1;
            loop[(at + i) % length] += samples[from + i] * gain;
        }
    }
    return loop;
}
