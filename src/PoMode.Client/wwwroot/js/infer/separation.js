// Tier 2 (ClientDelegated) stem separation, the page's half: fetches the upload, decodes it at the
// model's 44.1 kHz, hands it to separation-thread.js, and posts the vocal stem to the server, which
// derives the instrumental as mix minus vocals. The same shape as pitch-worker.js's runDelegated.
//
// A browser without WebGPU declines anything longer than a short clip rather than trying: MDX-Net on
// WASM runs slower than the song plays, and a declined job falls through on the server at once
// where an attempted one would hold the only worker for many minutes.

import { probeCapability } from './pitch-worker.js';
import { encodeWav } from '../capture/wav.js';
import { MDX } from './mdx.js';

/** Longest song the WASM backend is asked to separate. */
const WASM_MAX_SECONDS = 60;

const startedJobs = new Set();

/** Runs the whole delegation for one job, once. Never throws: failure becomes a decline. */
export async function runDelegated(jobId) {
    if (startedJobs.has(jobId)) {
        return;
    }
    startedJobs.add(jobId);
    let reason = 'separation failed in the browser';
    try {
        const backend = await probeCapability();
        const response = await fetch(`api/analysis/${jobId}/stems/mix`);
        if (!response.ok) {
            throw new Error(`mix fetch failed: ${response.status}`);
        }
        const decoded = await new OfflineAudioContext(2, 1, MDX.sampleRate).decodeAudioData(await response.arrayBuffer());
        if (backend === null || (backend !== 'webgpu' && decoded.duration > WASM_MAX_SECONDS)) {
            reason = `no WebGPU to separate ${Math.round(decoded.duration)} s of audio in time`;
            throw new Error(reason);
        }

        const left = decoded.getChannelData(0).slice();
        const right = decoded.getChannelData(decoded.numberOfChannels > 1 ? 1 : 0).slice();
        const vocals = await separateInWorker(left, right, backend);

        const frames = vocals.left.length;
        const interleaved = new Float32Array(frames * 2);
        for (let i = 0; i < frames; i++) {
            interleaved[2 * i] = vocals.left[i];
            interleaved[(2 * i) + 1] = vocals.right[i];
        }
        const posted = await fetch(`api/analysis/${jobId}/client-stems`, {
            method: 'POST',
            headers: { 'Content-Type': 'audio/wav' },
            body: encodeWav([interleaved], frames, MDX.sampleRate, 2),
        });
        if (!posted.ok) {
            console.error(`client-stems rejected: ${posted.status} ${await posted.text()}`);
        }
    } catch (error) {
        console.error('Browser stem separation did not run; the server will fall through.', error);
        await fetch(`api/analysis/${jobId}/client-stems?reason=${encodeURIComponent(reason)}`, { method: 'DELETE' })
            .catch(() => {});
    }
}

function separateInWorker(left, right, backend) {
    return new Promise((resolve, reject) => {
        const worker = new Worker(new URL('./separation-thread.js', import.meta.url), { type: 'module' });
        worker.onmessage = ({ data }) => {
            if (data.progress !== undefined) {
                document.body.dataset.separationProgress = data.progress.toFixed(2);
                return;
            }
            worker.terminate();
            if (data.error) {
                reject(new Error(data.error));
            } else {
                resolve(data);
            }
        };
        worker.onerror = event => {
            worker.terminate();
            reject(new Error(event.message));
        };
        worker.postMessage({ left, right, backend }, [left.buffer, right.buffer]);
    });
}
