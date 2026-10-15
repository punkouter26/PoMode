// A second opinion on the key, from Essentia (Music Technology Group, UPF; AGPL-3.0) compiled to
// WebAssembly: its KeyExtractor over a minute of the song's mix, in this browser. Asked for on a click
// in the "Why this mode?" drawer, never on load: the library is about 3 MB, fetched once and cached.
// It reports what Essentia said; how that stands against the mode is worded server-side
// (KeyAgreement), as every musical statement is.

const ESSENTIA_WASM = 'https://cdn.jsdelivr.net/npm/essentia.js@0.1.3/dist/essentia-wasm.es.js';
const ESSENTIA_CORE = 'https://cdn.jsdelivr.net/npm/essentia.js@0.1.3/dist/essentia.js-core.es.js';
const RATE = 44100;
const SECONDS = 60;
const NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
const FLATS = { Db: 1, Eb: 3, Gb: 6, Ab: 8, Bb: 10 };

let essentia = null;

/// { pitchClass, major, strength } for the job's mix, or null when it could not be read.
export async function read(jobId) {
    try {
        if (!essentia) {
            const [{ EssentiaWASM }, { default: Essentia }] = await Promise.all([import(ESSENTIA_WASM), import(ESSENTIA_CORE)]);
            essentia = new Essentia(EssentiaWASM);
        }
        const response = await fetch(`api/analysis/${jobId}/stems/mix`);
        if (!response.ok) {
            return null;
        }
        const context = new OfflineAudioContext(1, 1, RATE);
        const decoded = await context.decodeAudioData(await response.arrayBuffer());
        // The middle minute: intros and outros are where a song is least in its key.
        const length = Math.min(decoded.length, SECONDS * RATE);
        const from = Math.max(0, Math.floor((decoded.length - length) / 2));
        const mono = new Float32Array(length);
        for (let channel = 0; channel < decoded.numberOfChannels; channel++) {
            const data = decoded.getChannelData(channel);
            for (let i = 0; i < length; i++) mono[i] += data[from + i] / decoded.numberOfChannels;
        }
        const vector = essentia.arrayToVector(mono);
        const key = essentia.KeyExtractor(vector, true, 4096, 4096, 12, 3500, 60, 25, 0.2, 'bgate', RATE);
        vector.delete?.();
        const pitchClass = NAMES.indexOf(key.key) >= 0 ? NAMES.indexOf(key.key) : FLATS[key.key];
        if (pitchClass === undefined) {
            return null;
        }
        return { pitchClass, major: key.scale === 'major', strength: key.strength };
    } catch (error) {
        console.warn('Essentia could not read the key', error);
        return null;
    }
}
