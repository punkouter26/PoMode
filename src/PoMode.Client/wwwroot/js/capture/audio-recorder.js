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
        recordedChunks.push(new Float32Array(event.inputBuffer.getChannelData(0)));
    };

    inputNode.connect(processorNode);
    processorNode.connect(audioContext.destination);
    return true;
}

export async function stopRecording() {
    isRecording = false;
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

