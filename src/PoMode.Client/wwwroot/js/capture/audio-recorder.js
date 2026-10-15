// Home-page microphone capture → 16-bit mono PCM WAV bytes, ready for the normal upload path.
// Asks for the raw signal deliberately: echo cancellation and automatic gain help a pitch tracker
// but hurt an analyzer. The Live page captures with the opposite constraints for that reason.

import { encodeWav } from './wav.js';

let mediaStream = null;
let audioContext = null;
let inputNode = null;
let processorNode = null;
let recordedChunks = [];
let sampleRate = 44100;
let isRecording = false;

/// The loudest sample of the latest buffer (0..1), read by the level meter, and the meter's frame.
let peak = 0;
let meterFrame = 0;

/// The meter's scale: -60 dBFS at the left end, 0 dBFS (full scale) at the right.
const FLOOR_DB = -60;
/// At or above this a sample is clipping, and the take will be distorted.
const CLIP = 0.99;

export async function startRecording() {
    recordedChunks = [];
    isRecording = true;

    mediaStream = await navigator.mediaDevices.getUserMedia({
        audio: {
            channelCount: 1,
            echoCancellation: false,
            autoGainControl: false,
            noiseSuppression: false,
        },
    });

    audioContext = new AudioContext({ sampleRate: 44100 });
    sampleRate = audioContext.sampleRate;

    inputNode = audioContext.createMediaStreamSource(mediaStream);
    processorNode = audioContext.createScriptProcessor(4096, 1, 1);
    processorNode.onaudioprocess = (event) => {
        if (!isRecording) return;
        const data = event.inputBuffer.getChannelData(0);
        recordedChunks.push(new Float32Array(data));
        let loudest = 0;
        for (let i = 0; i < data.length; i++) {
            const value = Math.abs(data[i]);
            if (value > loudest) loudest = value;
        }
        peak = loudest;
    };

    inputNode.connect(processorNode);
    processorNode.connect(audioContext.destination);
    return true;
}

/// Draws the microphone's level on `element` while recording: the loudest sample of each buffer, on a
/// decibel scale, falling back smoothly between hits so a drum's attack stays readable. Written as a
/// CSS custom property (--level, 0..1) and as the meter's ARIA value, straight from here, so following
/// it costs Blazor no renders. data-clip marks a take that is hitting full scale.
export function watchLevel(element) {
    cancelAnimationFrame(meterFrame);
    if (!element) return;
    let shown = 0;
    let clipUntil = 0;
    const draw = (now) => {
        if (!isRecording) {
            element.style.setProperty('--level', '0');
            element.dataset.clip = '0';
            return;
        }
        const db = peak > 0 ? 20 * Math.log10(peak) : FLOOR_DB;
        const target = Math.min(1, Math.max(0, (db - FLOOR_DB) / -FLOOR_DB));
        // Rise at once, fall over about half a second: a meter that drops as fast as it climbs flickers.
        shown = target >= shown ? target : Math.max(target, shown - 0.03);
        if (peak >= CLIP) clipUntil = now + 1000;
        element.style.setProperty('--level', shown.toFixed(3));
        element.setAttribute('aria-valuenow', String(Math.round(shown * 100)));
        element.dataset.clip = now < clipUntil ? '1' : '0';
        meterFrame = requestAnimationFrame(draw);
    };
    meterFrame = requestAnimationFrame(draw);
}

export async function stopRecording() {
    isRecording = false;
    cancelAnimationFrame(meterFrame);
    peak = 0;
    if (processorNode) { processorNode.disconnect(); processorNode = null; }
    if (inputNode) { inputNode.disconnect(); inputNode = null; }
    if (mediaStream) { mediaStream.getTracks().forEach((track) => track.stop()); mediaStream = null; }
    if (audioContext) { await audioContext.close(); audioContext = null; }
    return true;
}

export function getRecordedWavBytes() {
    let frames = 0;
    for (const chunk of recordedChunks) frames += chunk.length;
    return encodeWav(recordedChunks, frames, sampleRate);
}

