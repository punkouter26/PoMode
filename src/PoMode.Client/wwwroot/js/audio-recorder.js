// Live microphone capture → 16-bit mono PCM WAV bytes, ready for the normal upload path.
// Concept ported from the PoModeAg variant.

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
    let totalSamples = 0;
    for (const chunk of recordedChunks) totalSamples += chunk.length;

    const merged = new Float32Array(totalSamples);
    let offset = 0;
    for (const chunk of recordedChunks) { merged.set(chunk, offset); offset += chunk.length; }

    return new Uint8Array(encodeWav(merged, sampleRate));
}

function encodeWav(samples, rate) {
    const buffer = new ArrayBuffer(44 + samples.length * 2);
    const view = new DataView(buffer);

    writeString(view, 0, 'RIFF');
    view.setUint32(4, 36 + samples.length * 2, true);
    writeString(view, 8, 'WAVE');
    writeString(view, 12, 'fmt ');
    view.setUint32(16, 16, true);   // format chunk length
    view.setUint16(20, 1, true);    // raw 16-bit PCM
    view.setUint16(22, 1, true);    // mono
    view.setUint32(24, rate, true);
    view.setUint32(28, rate * 2, true); // byte rate
    view.setUint16(32, 2, true);    // block align
    view.setUint16(34, 16, true);   // bits per sample
    writeString(view, 36, 'data');
    view.setUint32(40, samples.length * 2, true);

    let index = 44;
    for (let i = 0; i < samples.length; i++) {
        const s = Math.max(-1, Math.min(1, samples[i]));
        view.setInt16(index, s < 0 ? s * 0x8000 : s * 0x7FFF, true);
        index += 2;
    }
    return buffer;
}

function writeString(view, offset, text) {
    for (let i = 0; i < text.length; i++) {
        view.setUint8(offset + i, text.charCodeAt(i));
    }
}
