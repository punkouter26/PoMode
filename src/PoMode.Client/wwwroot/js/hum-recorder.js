// Mode Lab hum-along capture: the microphone recorded against the backing player's own clock.
//
// A third capture path beside audio-recorder.js and live-session.js, and for a third reason. Those
// two differ over whether the browser's cleanup helps or hurts the thing downstream; this one is
// the only path where the page is playing audio out of the speakers while the microphone is open,
// so it wants that cleanup badly — without echo cancellation every take comes back with the piano
// bleeding through it, and the pitch tracker faithfully transcribes the piano.
//
// The other job here is alignment. The server tiles the progression the user sang over across the
// take starting at sample zero, so sample zero has to be bar one: the count-in and the gap between
// "microphone is live" and "loop starts" are trimmed off the front. A take that kept them would
// slide every phrase the user sang onto the wrong chord.

import { encodeWav } from './wav.js';
import * as player from './modal-player.js';

const FRAME_SIZE = 4096;

let mediaStream = null;
let inputNode = null;
let processorNode = null;
let silentSink = null;
let chunks = [];
let sampleRate = 44100;
let captureStartTime = null;
let isCapturing = false;

// The encoded take is kept after finishCapture so it can be played back before the singer commits
// to saving it — hearing the take against the chords is the only way to judge it, and re-recording
// is cheaper than analyzing something you have not heard.
let lastTake = null;
let lastTakeBuffer = null;
let reviewSource = null;

// Mirrored onto the document like mixer.js/canvas.js/modal-player.js do, so Playwright can assert
// on the recorder without reaching into module internals.
function publishState() {
    try {
        const el = document.body;
        if (!el) return;
        el.dataset.humRecorder = isCapturing ? 'recording' : (reviewSource ? 'reviewing' : 'idle');
        el.dataset.humHasTake = lastTake && lastTake.length > 0 ? 'yes' : 'no';
        el.dataset.humSeconds = capturedSeconds().toFixed(2);
    } catch { }
}

function frameCount() {
    let frames = 0;
    for (const chunk of chunks) frames += chunk.length;
    return frames;
}

/// Seconds of audio captured so far. Polled by the page for its running take timer and its own
/// length cap, so the cap lives in one place rather than being half-enforced here.
export function capturedSeconds() {
    return frameCount() / sampleRate;
}

/// Opens the microphone and starts accumulating. Resolves once audio is actually being captured,
/// so the caller can start the backing loop knowing the take has already begun.
export async function beginCapture() {
    if (isCapturing) return true;

    const ctx = player.getSharedContext();
    if (!ctx) return false;
    if (ctx.state === 'suspended') {
        await ctx.resume().catch(() => { });
    }

    mediaStream = await navigator.mediaDevices.getUserMedia({
        audio: {
            channelCount: 1,
            // Deliberately the opposite of the Home recorder's constraints. The backing chords are
            // coming out of these speakers and into this microphone; a take with the piano in it
            // gets analyzed as the piano, which is the one result this feature must not produce.
            echoCancellation: true,
            autoGainControl: true,
            noiseSuppression: true,
        },
    });

    chunks = [];
    captureStartTime = null;
    sampleRate = ctx.sampleRate;

    inputNode = ctx.createMediaStreamSource(mediaStream);
    processorNode = ctx.createScriptProcessor(FRAME_SIZE, 1, 1);
    processorNode.onaudioprocess = (event) => {
        if (!isCapturing) return;
        const frame = event.inputBuffer.getChannelData(0);
        if (captureStartTime === null) {
            // The callback fires once the frame has already been captured, so the audio it carries
            // began one frame-length ago. That is as precise as a ScriptProcessor gets; what is
            // left is a few milliseconds, far under a sixteenth note at any tempo the lab offers.
            captureStartTime = ctx.currentTime - frame.length / sampleRate;
        }
        chunks.push(new Float32Array(frame));
    };

    // A muted sink: some browsers will not run a ScriptProcessor whose output reaches no
    // destination, and routing the microphone to the speakers instead would be a feedback loop.
    silentSink = ctx.createGain();
    silentSink.gain.value = 0;
    inputNode.connect(processorNode);
    processorNode.connect(silentSink);
    silentSink.connect(ctx.destination);

    isCapturing = true;
    publishState();
    return true;
}

/// Four clicks at the given tempo, resolving when the last one has sounded. Gives the singer the
/// pulse before the loop starts; it lands before bar one, so the trim takes it back off the take.
export function countIn(bpm, beats) {
    const ctx = player.getSharedContext();
    if (!ctx) return Promise.resolve();

    const secondsPerBeat = 60.0 / (bpm > 0 ? bpm : 100.0);
    const count = beats > 0 ? beats : 4;
    const start = ctx.currentTime + 0.12;
    for (let i = 0; i < count; i++) {
        click(ctx, start + i * secondsPerBeat, i === 0);
    }
    const endsIn = (start + count * secondsPerBeat) - ctx.currentTime;
    return new Promise((resolve) => setTimeout(resolve, Math.max(0, endsIn * 1000)));
}

function click(ctx, when, isDownbeat) {
    const osc = ctx.createOscillator();
    osc.type = 'square';
    osc.frequency.setValueAtTime(isDownbeat ? 1600 : 1050, when);

    const env = ctx.createGain();
    env.gain.setValueAtTime(0.0001, when);
    env.gain.exponentialRampToValueAtTime(isDownbeat ? 0.22 : 0.13, when + 0.004);
    env.gain.exponentialRampToValueAtTime(0.0001, when + 0.07);

    osc.connect(env);
    env.connect(ctx.destination);
    osc.start(when);
    osc.stop(when + 0.09);
    osc.onended = () => { try { env.disconnect(); } catch { } };
}

/// Shorter than this after trimming and there is no take, only the tail of a count-in. Encoding it
/// anyway would upload a valid-but-empty WAV and queue a job with nothing in it to analyze.
const MIN_TAKE_SECONDS = 0.5;

/// Closes the microphone and returns the take as 16-bit mono PCM WAV bytes, trimmed so sample zero
/// is the loop's downbeat. Returns an empty array — never null, which does not survive the byte-array
/// marshalling — when nothing usable was captured.
export function finishCapture() {
    // Read the downbeat before anything is torn down — the caller stops the player afterwards, and
    // a stopped player no longer knows when its pass through the loop began.
    const playbackStart = player.getPlaybackStartTime();
    isCapturing = false;
    teardown();

    const frames = frameCount();
    let skip = 0;
    if (playbackStart !== null && captureStartTime !== null && playbackStart > captureStartTime) {
        skip = Math.min(Math.round((playbackStart - captureStartTime) * sampleRate), frames);
    }

    const kept = frames - skip;
    if (kept < MIN_TAKE_SECONDS * sampleRate) {
        chunks = [];
        clearTake();
        return new Uint8Array(0);
    }

    const bytes = encodeWav(trimLeading(chunks, skip), kept, sampleRate);
    chunks = [];
    lastTake = bytes;
    lastTakeBuffer = null;
    publishState();
    return bytes;
}

/// Plays the take back with the chords underneath it, both scheduled off one instant so they stay in
/// step. Hearing the voice alone says nothing about whether the phrase landed — the whole question is
/// how it sits against the harmony. Resolves with the take's length in seconds, or 0 if there is none.
export async function reviewTake(backingNotes, loopDuration, dotNetHelper) {
    const ctx = player.getSharedContext();
    if (!ctx || !lastTake || lastTake.length === 0) return 0;
    if (ctx.state === 'suspended') {
        await ctx.resume().catch(() => { });
    }

    if (!lastTakeBuffer) {
        try {
            // decodeAudioData detaches the buffer it is handed, which would leave lastTake empty and
            // unsaveable — so it decodes a copy.
            lastTakeBuffer = await ctx.decodeAudioData(lastTake.slice().buffer);
        } catch {
            return 0;
        }
    }

    stopReview();

    // Far enough ahead that both the recording and the first scheduled chord are comfortably in the
    // future when the audio thread next looks.
    const startAt = ctx.currentTime + 0.2;

    reviewSource = ctx.createBufferSource();
    reviewSource.buffer = lastTakeBuffer;
    reviewSource.connect(ctx.destination);
    reviewSource.onended = () => {
        reviewSource = null;
        // The chords loop indefinitely; the take does not, so the recording's end is what ends the
        // review. Without this the backing would keep playing over silence.
        try { player.stop(); } catch { }
        publishState();
        if (dotNetHelper) {
            try { dotNetHelper.invokeMethodAsync('OnHumReviewEnded'); } catch { }
        }
    };
    reviewSource.start(startAt);

    // Chords only, from the same instant — the take already carries the melody.
    player.play([], backingNotes || [], loopDuration, dotNetHelper, startAt);

    publishState();
    return lastTakeBuffer.duration;
}

export function stopReview() {
    if (reviewSource) {
        // Drop the handler first: this is a deliberate stop, and letting onended fire would report
        // it to Blazor as the take running to its end.
        reviewSource.onended = null;
        try { reviewSource.stop(); } catch { }
        try { reviewSource.disconnect(); } catch { }
        reviewSource = null;
    }
    try { player.stop(); } catch { }
    publishState();
}

/// Forgets the take — a redo, or a take that has been saved.
export function clearTake() {
    stopReview();
    lastTake = null;
    lastTakeBuffer = null;
    publishState();
}

/// Drops the take without encoding it — a redo, or leaving the page mid-record.
export function cancelCapture() {
    isCapturing = false;
    teardown();
    chunks = [];
    publishState();
}

function trimLeading(all, skip) {
    const kept = [];
    let remaining = skip;
    for (const chunk of all) {
        if (remaining >= chunk.length) {
            remaining -= chunk.length;
            continue;
        }
        kept.push(remaining > 0 ? chunk.subarray(remaining) : chunk);
        remaining = 0;
    }
    return kept;
}

function teardown() {
    if (processorNode) {
        processorNode.onaudioprocess = null;
        try { processorNode.disconnect(); } catch { }
        processorNode = null;
    }
    if (inputNode) { try { inputNode.disconnect(); } catch { } inputNode = null; }
    if (silentSink) { try { silentSink.disconnect(); } catch { } silentSink = null; }
    if (mediaStream) {
        // Stopping the tracks is what turns the browser's recording indicator off; leaving them
        // live would keep the microphone open for the rest of the session.
        mediaStream.getTracks().forEach((track) => track.stop());
        mediaStream = null;
    }
    captureStartTime = null;
}

export function dispose() {
    cancelCapture();
    clearTake();
}
