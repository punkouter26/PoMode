// Tier 2 (ClientDelegated) in-browser pitch tracking: fetches the vocal stem and the pinned Basic
// Pitch model from our own origin, runs inference through onnxruntime-web (WebGPU when a real
// adapter exists, else WASM SIMD — the verified path on this project's hardware), decodes with the
// shared decoder port, and POSTs the note events back to the server. Plain ES module: no npm, no
// bundler, no CDN — /web-runtime/* is served and SHA-256-verified by the API's ModelRegistry.
//
// The windowing below is a direct port of OnnxPitchTracker.cs — same overlap (30 frames), same
// half-overlap leading pad and per-window trim, same global stitching — so the browser and the
// local tier produce comparable results from the same model file.

import { decodeNotes } from './basic-pitch-decoder.js';

// This pinned nmp.onnx export takes [1, 43844, 1] audio at 22.05 kHz. Used only when the runtime
// does not expose input metadata; the value is a property of the pinned model file, not a guess.
const FALLBACK_WINDOW_SAMPLES = 43844;
const TARGET_SAMPLE_RATE = 22050;
const MIN_MIDI = 21;
const OVERLAP_FRAMES = 30;
const NOTE_OUTPUT = 'StatefulPartitionedCall:1';
const ONSET_OUTPUT = 'StatefulPartitionedCall:2';

const startedJobs = new Set();

/** 'webgpu' when a real adapter exists, 'wasm' when WebAssembly is available, else null. */
export async function probeCapability() {
    try {
        if (navigator.gpu && await navigator.gpu.requestAdapter() !== null) {
            return 'webgpu';
        }
    } catch {
        // navigator.gpu can exist and still throw; fall through to WASM.
    }
    return typeof WebAssembly === 'object' ? 'wasm' : null;
}

/**
 * Runs the whole delegation for one job. Safe to call repeatedly — a job is only processed once.
 * Errors are logged and swallowed: the server's Tier2 timeout owns failure handling and will fall
 * through to the next tier; a broken browser must never wedge anything.
 */
export async function runDelegated(jobId) {
    if (startedJobs.has(jobId)) {
        return;
    }
    startedJobs.add(jobId);
    try {
        const backend = await probeCapability();
        if (backend === null) {
            return;
        }

        const [samples, model, ort] = await Promise.all([
            fetchVocalsMono22k(jobId),
            fetchModel(),
            loadRuntime(),
        ]);

        const session = await ort.InferenceSession.create(model, { executionProviders: [backend] });
        const notes = await transcribe(ort, session, samples);

        const response = await fetch(`api/analysis/${jobId}/client-result`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(notes),
        });
        if (!response.ok) {
            console.error(`client-result rejected: ${response.status} ${await response.text()}`);
        }
    } catch (error) {
        console.error('Browser pitch tracking failed; the server will fall through.', error);
    }
}

async function fetchVocalsMono22k(jobId) {
    const response = await fetch(`api/analysis/${jobId}/stems/vocals`);
    if (!response.ok) {
        throw new Error(`vocals stem fetch failed: ${response.status}`);
    }
    const bytes = await response.arrayBuffer();
    // decodeAudioData resamples to the context's rate, which is exactly the model's front-end rate.
    const context = new OfflineAudioContext(1, 1, TARGET_SAMPLE_RATE);
    const decoded = await context.decodeAudioData(bytes);
    const mono = new Float32Array(decoded.length);
    for (let channel = 0; channel < decoded.numberOfChannels; channel++) {
        const data = decoded.getChannelData(channel);
        for (let i = 0; i < data.length; i++) {
            mono[i] += data[i] / decoded.numberOfChannels;
        }
    }
    return mono;
}

async function fetchModel() {
    const response = await fetch('web-runtime/nmp.onnx');
    if (!response.ok) {
        throw new Error(`model fetch failed: ${response.status}`);
    }
    return new Uint8Array(await response.arrayBuffer());
}

async function loadRuntime() {
    const module = await import('/web-runtime/ort.all.bundle.min.mjs');
    const ort = module.default ?? module;
    // The bundle embeds its JS glue; only the .wasm binary is fetched, from our origin.
    ort.env.wasm.wasmPaths = '/web-runtime/';
    return ort;
}

async function transcribe(ort, session, samples) {
    const inputName = session.inputNames[0];
    const metaShape = session.inputMetadata?.[0]?.shape;
    const windowSamples = Array.isArray(metaShape) && typeof metaShape[1] === 'number' && metaShape[1] > 0
        ? metaShape[1]
        : FALLBACK_WINDOW_SAMPLES;

    const run = async windowData => {
        const feeds = { [inputName]: new ort.Tensor('float32', windowData, [1, windowSamples, 1]) };
        const results = await session.run(feeds);
        const note = results[NOTE_OUTPUT];
        const onset = results[ONSET_OUTPUT];
        if (!note || !onset) {
            throw new Error(`model outputs ${NOTE_OUTPUT}/${ONSET_OUTPUT} not found`);
        }
        return { note, onset };
    };

    // One probe inference on a zero window discovers the output frame grid (the C# executor reads
    // it from ONNX metadata, which onnxruntime-web does not expose for outputs).
    const probe = await run(new Float32Array(windowSamples));
    const outputFrames = probe.note.dims[1];
    const pitchCount = probe.note.dims[2];

    const samplesPerFrame = windowSamples / outputFrames;
    const framesPerSecond = TARGET_SAMPLE_RATE / samplesPerFrame;
    const overlapFrames = Math.min(OVERLAP_FRAMES, Math.floor(outputFrames / 2));
    const trimFrames = Math.floor(overlapFrames / 2);
    const overlapSamples = Math.round(overlapFrames * samplesPerFrame);
    const hopSamples = Math.max(1, windowSamples - overlapSamples);
    const leadingPadSamples = Math.round(trimFrames * samplesPerFrame);

    const padded = new Float32Array(leadingPadSamples + samples.length);
    padded.set(samples, leadingPadSamples);

    const totalFrames = Math.max(1, Math.ceil(samples.length / samplesPerFrame));
    const onsetsGlobal = Array.from({ length: totalFrames }, () => new Float32Array(pitchCount));
    const framesGlobal = Array.from({ length: totalFrames }, () => new Float32Array(pitchCount));

    for (let windowStart = 0; ; windowStart += hopSamples) {
        const windowData = new Float32Array(windowSamples);
        windowData.set(padded.subarray(windowStart, Math.min(windowStart + windowSamples, padded.length)));

        const { note, onset } = await run(windowData);
        const noteData = note.data;
        const onsetData = onset.data;

        const frameOffset = Math.round((windowStart - leadingPadSamples) / samplesPerFrame);
        const lastFrame = outputFrames - trimFrames;
        for (let f = trimFrames; f < lastFrame; f++) {
            const globalFrame = frameOffset + f;
            if (globalFrame < 0 || globalFrame >= totalFrames) {
                continue;
            }
            for (let p = 0; p < pitchCount; p++) {
                framesGlobal[globalFrame][p] = noteData[(f * pitchCount) + p];
                onsetsGlobal[globalFrame][p] = onsetData[(f * pitchCount) + p];
            }
        }

        if (windowStart + windowSamples >= padded.length) {
            break;
        }
    }

    return decodeNotes(onsetsGlobal, framesGlobal, framesPerSecond, MIN_MIDI);
}
