// node tests/loop-cut.check.mjs — the drum loop's splice, checked against a take whose hits are known.
import assert from 'node:assert/strict';
import { buildLoop, findOnsets, layOut, loopBars, loopFrameCount }
    from '../src/PoMode.Client/wwwroot/js/player/loop-cut.js';

const rate = 48000;
const playedBpm = 93;           // no click: the take is at its own tempo, and not steady
const targetBpm = 100;
const pattern = [0, 4, 6, 8, 12, 14];   // sixteenths within a bar
const steps = [...pattern, ...pattern.map((s) => s + 16), 32];   // two bars, then the closing "one"

// Each hit a 30 ms decaying burst, up to 8 ms early or late, after 0.3 s of lead-in silence.
const playedStep = 15 / playedBpm * rate;
const take = new Float32Array(Math.round(0.3 * rate + 34 * playedStep));
steps.forEach((step, k) => {
    const jitter = k === 0 ? 0 : ((k * 37) % 17 - 8) / 1000 * rate;
    const at = Math.round(0.3 * rate + step * playedStep + jitter);
    for (let i = 0; i < 0.03 * rate; i++) take[at + i] += 0.8 * Math.sin(i * 0.9) * Math.exp(-i / 300);
});

const onsets = findOnsets(take, rate);
assert.equal(onsets.length, steps.length, 'one onset per hit');

const { slices, sourceBpm, bars } = layOut(onsets, rate, targetBpm);
assert.ok(Math.abs(sourceBpm - playedBpm) < 1.5, `read the take as ${sourceBpm} BPM, played at ${playedBpm}`);
assert.deepEqual(slices.map((slice) => slice.step), steps, 'every hit on the sixteenth it was played on');
assert.equal(bars, 2, 'the closing hit on the bar line does not add a bar');

const loop = buildLoop(take, rate, targetBpm, slices, bars);
assert.equal(loop.length, loopFrameCount(targetBpm, bars, rate));
assert.equal(loop.length, 230400, 'two bars at 100 BPM is exactly 4.8 seconds');

// In the loop every hit starts within 6 ms of its grid line at the target tempo, jitter gone.
const targetStep = 15 / targetBpm * rate;
for (const step of steps.slice(0, -1)) {
    const line = Math.round(step * targetStep);
    let first = -1;
    for (let i = Math.max(0, line - 2000); i < line + 2000 && first < 0; i++) {
        if (Math.abs(loop[i]) > 0.2) first = i;
    }
    assert.ok(first >= line && first - line < 0.006 * rate, `step ${step} lands ${first - line} frames off`);
}

// Loops come in 1, 2, 4 or 8 bars: the longest the take fills.
assert.deepEqual([1, 2, 3, 4, 5, 7, 8, 12].map(loopBars), [1, 2, 2, 4, 4, 4, 8, 8]);

// Silence has nothing to cut.
assert.equal(findOnsets(new Float32Array(rate), rate).length, 0);
assert.equal(layOut([], rate, targetBpm).slices.length, 0);

console.log('loop-cut ok');
