// Small reads over decoded audio that several modules make the same way: a mixdown to one channel,
// and a min/max sketch for drawing a waveform. Pure arithmetic, no audio graph and no DOM.

/// The channels of an AudioBuffer averaged into one: one signal to find hits or pitch on.
export function toMono(buffer) {
    const mono = new Float32Array(buffer.length);
    for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
        const data = buffer.getChannelData(channel);
        for (let i = 0; i < data.length; i++) mono[i] += data[i] / buffer.numberOfChannels;
    }
    return mono;
}

/// `columns` [low, high] pairs over `samples`, flattened: column c is at 2c and 2c + 1. `stride`
/// skips samples inside a column, for a backdrop that needs no exactness.
export function peaks(samples, columns, stride = 1) {
    const out = new Float32Array(columns * 2);
    const perColumn = samples.length / columns;
    for (let column = 0; column < columns; column++) {
        let low = 0;
        let high = 0;
        const end = Math.min(Math.floor((column + 1) * perColumn), samples.length);
        for (let i = Math.floor(column * perColumn); i < end; i += stride) {
            const value = samples[i];
            if (value < low) low = value;
            if (value > high) high = value;
        }
        out[column * 2] = low;
        out[column * 2 + 1] = high;
    }
    return out;
}
