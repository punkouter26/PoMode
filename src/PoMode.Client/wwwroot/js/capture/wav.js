// One 16-bit mono PCM WAV encoder for every microphone path in the app.
//
// Three capture paths exist on purpose and must stay separate: the Home recorder asks for the raw
// signal (no echo cancellation, no automatic gain) because the analyzer works better on untouched
// audio; the Live session wants the browser's cleanup because it is feeding a pitch tracker; and
// the Mode Lab's hum recorder needs echo cancellation most of all, because it is the only one that
// records while the page is playing a backing track out of the speakers.
// What they had no business duplicating was this header, which they each carried a copy of.

/// Encodes `chunks` (any iterable of Float32Array) totalling `frameCount` frames at `sampleRate`,
/// samples interleaved when `channels` is more than one (the browser separator's stereo vocal stem).
export function encodeWav(chunks, frameCount, sampleRate, channels = 1) {
    const dataBytes = frameCount * channels * 2;
    const view = new DataView(new ArrayBuffer(44 + dataBytes));
    const writeText = (offset, text) => {
        for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i));
    };

    writeText(0, 'RIFF');
    view.setUint32(4, 36 + dataBytes, true);
    writeText(8, 'WAVE');
    writeText(12, 'fmt ');
    view.setUint32(16, 16, true);             // fmt chunk size
    view.setUint16(20, 1, true);              // PCM, uncompressed
    view.setUint16(22, channels, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, sampleRate * channels * 2, true); // byte rate
    view.setUint16(32, channels * 2, true);              // block align
    view.setUint16(34, 16, true);             // bits per sample
    writeText(36, 'data');
    view.setUint32(40, dataBytes, true);

    let offset = 44;
    for (const chunk of chunks) {
        for (let i = 0; i < chunk.length; i++, offset += 2) {
            const sample = Math.max(-1, Math.min(1, chunk[i]));
            view.setInt16(offset, sample < 0 ? sample * 0x8000 : sample * 0x7fff, true);
        }
    }
    return new Uint8Array(view.buffer);
}
