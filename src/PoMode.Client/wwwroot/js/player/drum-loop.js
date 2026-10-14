// The Mode Lab's drum loop: a recording from the library, spliced to the lab's tempo, played locked
// to the chord loop and saved as a WAV. The recording arrives the way every other one does, through
// the Library page; this only fetches the job's original audio back. The splice itself is loop-cut.js.
//
// It plays on modal-player.js's AudioContext and starts from that player's own downbeat, for the
// reason the hum recorder shares it: two clocks cannot be held in step, one clock needs no holding.

import { encodeWav } from '../capture/wav.js';
import { STEPS_PER_BAR, buildLoop, findOnsets, layOut, pulseBpm } from './loop-cut.js';
import { getPlaybackStartTime, getSharedContext } from './modal-player.js';

// A loop is bars, not a song: eight bars at 40 BPM is 48 s, and the tempo search is quadratic in hits.
const MAX_SECONDS = 120;
// Under the chords and melody, not over them.
const GAIN = 0.9;

let ctx = null;
let take = null;        // { samples, rate, onsets, pulse }
let cut = null;         // { loop, buffer, bpm, bars, sourceBpm, hits, steps }
let source = null;
let canvas = null;
let backdrop = null;    // the still picture (grid, waveform, markers); the playhead is painted over it
let colors = null;      // { track, playhead }: the playhead's tokens, read once per splice rather than per frame
let startedAt = 0;      // the audio-clock instant the loop last started from its first sample
let frameId = 0;

// Canvas bands, in CSS pixels: labels, then the hit markers, the waveform, and the progress bar.
const LABEL_BAND = 16;
const MARKER_BAND = 12;
const PROGRESS_BAND = 8;
const LABEL_FONT = 12;  // the app's floor for text, on a canvas too

// Mirrored onto the document like the other audio modules, so Playwright can assert on the loop
// without reaching into this one.
function publish() {
    try {
        const data = document.body.dataset;
        data.drumLoop = source ? 'playing' : (cut ? 'ready' : 'idle');
        data.drumLoopSeconds = cut ? (cut.loop.length / take.rate).toFixed(3) : '0';
        data.drumLoopHits = cut ? String(cut.hits) : '0';
        if (!source) data.drumLoopProgress = '0.00';
    } catch { }
}

/// Fetches a library job's original audio and finds its hits. Answers 'ok', or why not: 'unreadable'
/// (missing, or not audio the browser can decode), 'long', or 'empty' (no hits to cut at).
export async function load(jobId) {
    clear();
    ctx = getSharedContext();
    let buffer;
    try {
        const response = await fetch(`api/analysis/${jobId}/stems/mix`);
        if (!response.ok) return 'unreadable';
        buffer = await ctx.decodeAudioData(await response.arrayBuffer());
    } catch {
        return 'unreadable';
    }
    if (buffer.duration > MAX_SECONDS) return 'long';

    // Mixed to mono: the hits are found on one signal, and one signal is what gets cut.
    const samples = new Float32Array(buffer.length);
    for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
        const data = buffer.getChannelData(channel);
        for (let i = 0; i < data.length; i++) samples[i] += data[i] / buffer.numberOfChannels;
    }
    const onsets = findOnsets(samples, buffer.sampleRate);
    if (onsets.length === 0) return 'empty';

    // The pulse search is the expensive part and does not depend on the tempo, so it runs once here.
    take = { samples, rate: buffer.sampleRate, onsets, pulse: pulseBpm(onsets, buffer.sampleRate) };
    return 'ok';
}

/// Splices the loaded recording to `bpm`. `bars` 0 keeps every bar that was played; a number keeps
/// that many from the start. Cheap enough to run again on every tempo change, and it picks the
/// transport back up if the lab is playing.
export function build(bpm, bars, canvasElement) {
    stop();
    canvas = canvasElement;
    if (!take) return false;
    const layout = layOut(take.onsets, take.rate, bpm, take.pulse);
    const length = bars > 0 ? bars : layout.bars;
    const kept = layout.slices.filter((slice) => slice.step < length * STEPS_PER_BAR);
    const loop = buildLoop(take.samples, take.rate, bpm, layout.slices, length);
    // Built once per splice; every play and seek after that starts a new source over the same buffer.
    const buffer = ctx.createBuffer(1, loop.length, take.rate);
    buffer.copyToChannel(loop, 0);
    cut = {
        loop,
        buffer,
        bpm,
        bars: length,
        sourceBpm: layout.sourceBpm,
        hits: kept.length,
        steps: kept.map((slice) => slice.step),
    };
    draw();
    sync();
    return true;
}

/// The tempo the loaded recording was played at. The pulse is unambiguous but which note value it
/// is (beat, eighth, sixteenth) is not, so the reading nearest `hintBpm` is the one returned.
export function playedBpm(hintBpm) {
    return take ? layOut(take.onsets, take.rate, hintBpm, take.pulse).sourceBpm : 0;
}

// Read back as primitives rather than one object, so nothing depends on how interop deserializes.
export function bars() { return cut ? cut.bars : 0; }
export function hits() { return cut ? cut.hits : 0; }
export function sourceBpm() { return cut ? cut.sourceBpm : 0; }

/// Starts the loop where the lab's chord loop currently is, or stops it if the lab is not playing.
/// Called after every play, pause, stop and seek. The loop repeats on its own length, so drums of a
/// bar count that divides the progression's stay on its bar one for as long as it runs; any other
/// length drifts round it as a polymeter, which is what was asked for by picking it.
export function sync() {
    stop();
    const downbeat = getPlaybackStartTime();
    if (!cut || !ctx || downbeat === null) {
        publish();
        return false;
    }
    const buffer = cut.buffer;
    const gain = ctx.createGain();
    gain.gain.value = GAIN;
    source = ctx.createBufferSource();
    source.buffer = buffer;
    source.loop = true;
    source.connect(gain);
    gain.connect(ctx.destination);
    // A moment ahead, entered at the point in the loop that moment falls on.
    const when = ctx.currentTime + 0.03;
    const seconds = buffer.duration;
    const offset = (((when - downbeat) % seconds) + seconds) % seconds;
    source.start(when, offset);
    startedAt = when - offset;
    frameId = requestAnimationFrame(tick);
    publish();
    return true;
}

export function stop() {
    if (!source) return;
    try { source.stop(); } catch { }
    try { source.disconnect(); } catch { }
    source = null;
    cancelAnimationFrame(frameId);
    paint(null);
    publish();
}

// One frame of the playhead. Read off the audio clock, so what is drawn is what is being heard.
function tick() {
    if (!source || !cut) return;
    const seconds = cut.loop.length / take.rate;
    const progress = (Math.max(ctx.currentTime - startedAt, 0) % seconds) / seconds;
    paint(progress);
    const shown = progress.toFixed(2);
    if (document.body.dataset.drumLoopProgress !== shown) document.body.dataset.drumLoopProgress = shown;
    frameId = requestAnimationFrame(tick);
}

/// Saves the loop as 16-bit mono WAV, named for the tempo and length a sampler will ask for.
export function exportWav() {
    if (!cut) return;
    const bytes = encodeWav([cut.loop], cut.loop.length, take.rate);
    const link = document.createElement('a');
    link.href = URL.createObjectURL(new Blob([bytes], { type: 'audio/wav' }));
    link.download = `drum-loop-${Math.round(cut.bpm)}bpm-${cut.bars}bar.wav`;
    link.click();
    setTimeout(() => URL.revokeObjectURL(link.href), 1000);
}

// The still picture, drawn once per splice: the bar and beat grid, the waveform, a marker where each
// hit was put on the grid (the sync), and the loop's two ends. Kept on a canvas of its own so the
// playhead can be painted over it every frame without redrawing a few hundred thousand samples.
function draw() {
    if (!canvas || !cut) return;
    const ratio = devicePixelRatio;
    const width = canvas.width = Math.max(1, Math.round(canvas.clientWidth * ratio));
    const height = canvas.height = Math.max(1, Math.round(canvas.clientHeight * ratio));
    backdrop = document.createElement('canvas');
    backdrop.width = width;
    backdrop.height = height;
    const g = backdrop.getContext('2d');
    const style = getComputedStyle(canvas);
    const token = (name) => style.getPropertyValue(name);
    colors = { track: token('--pm-fg-muted'), playhead: token('--pm-playhead') };
    const loop = cut.loop;

    const waveTop = (LABEL_BAND + MARKER_BAND) * ratio;
    const waveBottom = height - PROGRESS_BAND * ratio;
    const half = (waveBottom - waveTop) / 2;
    const mid = waveTop + half;
    const line = Math.ceil(ratio);

    // Beat lines, bar lines darker, bars numbered from the second on (the first is the loop's start).
    const beats = cut.bars * 4;
    g.font = `${LABEL_FONT * ratio}px system-ui, sans-serif`;
    g.textBaseline = 'top';
    g.fillStyle = token('--pm-fg-muted');
    for (let beat = 1; beat < beats; beat++) {
        const x = Math.round(beat / beats * width);
        g.globalAlpha = beat % 4 === 0 ? 0.9 : 0.4;
        g.fillRect(x, waveTop, line, waveBottom - waveTop);
        if (beat % 4 === 0) {
            g.globalAlpha = 1;
            g.fillText(String(beat / 4 + 1), x + 4 * ratio, 2 * ratio);
        }
    }
    g.globalAlpha = 1;

    g.fillStyle = token('--pm-accent');
    const perPixel = loop.length / width;
    for (let x = 0; x < width; x++) {
        let low = 0;
        let high = 0;
        for (let i = Math.floor(x * perPixel); i < (x + 1) * perPixel && i < loop.length; i++) {
            if (loop[i] < low) low = loop[i];
            if (loop[i] > high) high = loop[i];
        }
        low = Math.max(-1, low);
        high = Math.min(1, high);
        g.fillRect(x, mid - high * half, 1, Math.max(1, (high - low) * half));
    }

    // Sync markers: one per hit, on the sixteenth it was given. A hit's attack should rise from it.
    g.fillStyle = token('--pm-ok');
    const steps = cut.bars * STEPS_PER_BAR;
    const tip = 5 * ratio;
    for (const step of cut.steps) {
        const x = Math.round(step / steps * width);
        g.beginPath();
        g.moveTo(x - tip, waveTop - MARKER_BAND * ratio + 2 * ratio);
        g.lineTo(x + tip, waveTop - MARKER_BAND * ratio + 2 * ratio);
        g.lineTo(x, waveTop);
        g.fill();
        g.globalAlpha = 0.35;
        g.fillRect(x, waveTop, line, waveBottom - waveTop);
        g.globalAlpha = 1;
    }

    // The loop's two ends: where a pass begins, and where it wraps back to the beginning.
    g.fillStyle = token('--pm-fg');
    const edge = 3 * ratio;
    g.fillRect(0, 0, edge, waveBottom);
    g.fillRect(width - edge, 0, edge, waveBottom);
    g.fillText('Start', edge + 4 * ratio, 2 * ratio);
    g.textAlign = 'right';
    g.fillText('End', width - edge - 4 * ratio, 2 * ratio);

    paint(null);
}

// The backdrop plus what moves: the progress bar along the bottom and the playhead through the
// waveform. `progress` is 0..1 through the loop, or null when nothing is playing.
function paint(progress) {
    if (!canvas || !backdrop) return;
    const g = canvas.getContext('2d');
    const ratio = devicePixelRatio;
    const { width, height } = canvas;
    const band = PROGRESS_BAND * ratio;

    g.clearRect(0, 0, width, height);
    g.drawImage(backdrop, 0, 0);
    g.globalAlpha = 0.25;
    g.fillStyle = colors.track;
    g.fillRect(0, height - band + 2 * ratio, width, band - 2 * ratio);
    g.globalAlpha = 1;
    if (progress === null) return;

    const x = Math.round(progress * width);
    g.fillStyle = colors.playhead;
    g.fillRect(0, height - band + 2 * ratio, x, band - 2 * ratio);
    g.fillRect(x, 0, Math.ceil(2 * ratio), height);
}

/// Forgets the recording: another one picked, or none.
export function clear() {
    stop();
    take = null;
    cut = null;
    backdrop = null;
    if (canvas) {
        try { canvas.getContext('2d').clearRect(0, 0, canvas.width, canvas.height); } catch { }
    }
    publish();
}

/// The context is the player's, and the player closes it.
export function dispose() {
    clear();
    ctx = null;
}
