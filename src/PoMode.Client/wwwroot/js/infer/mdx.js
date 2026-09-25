// MDX-Net vocal separation, the signal processing around the model: a port of Ultimate Vocal
// Remover's reference `demix` for UVR-MDX-NET-Voc_FT - torch.stft/istft semantics (periodic Hann,
// centred, reflect-padded), UVR's chunking with a symmetric Hann cross-fade, the three lowest bins
// zeroed on the way in, and the model's output scaled by its compensation factor. The model call is
// injected, so this module is plain arithmetic that runs the same in a browser worker and in Node.
//
// The FFT length, 7680, is not a power of two (2^9 x 3 x 5), hence the small mixed-radix Stockham
// FFT below. Both channels go through one complex FFT (left + i*right) and are pulled apart by
// symmetry, which halves the work of the STFT and the ISTFT alike.

/** Voc_FT's settings, from UVR's model_data (hash 77d07b2667ddf05b9e3175941b4454a0). */
export const MDX = Object.freeze({ nFft: 7680, hop: 1024, dimF: 3072, dimT: 256, compensate: 1.021, sampleRate: 44100 });

/**
 * Separates the vocals from a stereo mix at 44.1 kHz. runModel takes the spectrogram as a
 * Float32Array laid out [4, dimF, dimT] (left re, left im, right re, right im) and resolves to the
 * model's output in the same layout. Returns { left, right } of the vocal stem, the mix's length.
 */
export async function separateVocals(left, right, runModel, onProgress = () => {}) {
    const { nFft, hop, dimT, compensate } = MDX;
    const chunk = hop * (dimT - 1);
    const trim = nFft / 2;
    const generated = chunk - (2 * trim);
    const length = left.length;
    const total = trim + length + generated + trim - (length % generated);

    const mixL = new Float32Array(total);
    const mixR = new Float32Array(total);
    mixL.set(left, trim);
    mixR.set(right, trim);

    const outL = new Float32Array(total);
    const outR = new Float32Array(total);
    const divider = new Float32Array(total);
    const stft = createStft();
    const step = chunk - nFft;
    const starts = [];
    for (let start = 0; start < total; start += step) {
        starts.push(start);
    }

    for (let index = 0; index < starts.length; index++) {
        const start = starts[index];
        const end = Math.min(start + chunk, total);
        const actual = end - start;
        const partL = new Float32Array(chunk);
        const partR = new Float32Array(chunk);
        partL.set(mixL.subarray(start, end));
        partR.set(mixR.subarray(start, end));

        const spec = stft.forward(partL, partR);
        const [vocalL, vocalR] = stft.inverse(await runModel(spec));

        // np.hanning: the symmetric window, over the part of the chunk that is real audio.
        for (let i = 0; i < actual; i++) {
            const w = actual > 1 ? 0.5 - (0.5 * Math.cos((2 * Math.PI * i) / (actual - 1))) : 1;
            outL[start + i] += vocalL[i] * w;
            outR[start + i] += vocalR[i] * w;
            divider[start + i] += w;
        }
        onProgress((index + 1) / starts.length);
    }

    const vocalsL = new Float32Array(length);
    const vocalsR = new Float32Array(length);
    for (let i = 0; i < length; i++) {
        const d = divider[trim + i];
        vocalsL[i] = d > 0 ? (outL[trim + i] / d) * compensate : 0;
        vocalsR[i] = d > 0 ? (outR[trim + i] / d) * compensate : 0;
    }
    return { left: vocalsL, right: vocalsR };
}

/** torch.stft / torch.istft for one MDX chunk, both channels at once. */
export function createStft() {
    const { nFft, hop, dimF, dimT } = MDX;
    const half = nFft / 2;
    const fft = createFft(nFft);
    const window = new Float32Array(nFft);
    for (let n = 0; n < nFft; n++) {
        window[n] = 0.5 - (0.5 * Math.cos((2 * Math.PI * n) / nFft)); // periodic Hann
    }
    const re = new Float64Array(nFft);
    const im = new Float64Array(nFft);
    const length = hop * (dimT - 1);

    const reflect = (signal, index) => {
        if (index < 0) {
            return signal[-index];
        }
        return index >= length ? signal[(2 * length) - 2 - index] : signal[index];
    };

    return {
        /** [4, dimF, dimT] from two channels of exactly one chunk; the lowest three bins zeroed. */
        forward(left, right) {
            const spec = new Float32Array(4 * dimF * dimT);
            const plane = dimF * dimT;
            for (let t = 0; t < dimT; t++) {
                const origin = (t * hop) - half;
                for (let n = 0; n < nFft; n++) {
                    re[n] = reflect(left, origin + n) * window[n];
                    im[n] = reflect(right, origin + n) * window[n];
                }
                fft.forward(re, im);
                for (let f = 3; f < dimF; f++) {
                    const g = (nFft - f) % nFft;
                    const at = (f * dimT) + t;
                    // Z = L + iR: L[f] = (Z[f] + conj Z[-f]) / 2, R[f] = (Z[f] - conj Z[-f]) / 2i.
                    spec[at] = (re[f] + re[g]) / 2;
                    spec[plane + at] = (im[f] - im[g]) / 2;
                    spec[(2 * plane) + at] = (im[f] + im[g]) / 2;
                    spec[(3 * plane) + at] = (re[g] - re[f]) / 2;
                }
            }
            return spec;
        },

        /** Two channels of one chunk from [4, dimF, dimT]; bins above dimF are zero, as UVR pads them. */
        inverse(spec) {
            const plane = dimF * dimT;
            const outL = new Float64Array(length + nFft);
            const outR = new Float64Array(length + nFft);
            const envelope = new Float64Array(length + nFft);
            for (let t = 0; t < dimT; t++) {
                re.fill(0);
                im.fill(0);
                for (let f = 0; f < dimF; f++) {
                    const at = (f * dimT) + t;
                    // irfft discards the imaginary part of the DC bin; packed, it would leak across.
                    const lr = spec[at];
                    const li = f === 0 ? 0 : spec[plane + at];
                    const rr = spec[(2 * plane) + at];
                    const ri = f === 0 ? 0 : spec[(3 * plane) + at];
                    // Y = L + iR, with each half-spectrum's Hermitian mirror filled in.
                    re[f] = lr - ri;
                    im[f] = li + rr;
                    if (f > 0) {
                        re[nFft - f] = lr + ri;
                        im[nFft - f] = rr - li;
                    }
                }
                fft.inverse(re, im);
                const origin = t * hop;
                for (let n = 0; n < nFft; n++) {
                    outL[origin + n] += re[n] * window[n];
                    outR[origin + n] += im[n] * window[n];
                    envelope[origin + n] += window[n] * window[n];
                }
            }
            const left = new Float32Array(length);
            const right = new Float32Array(length);
            for (let i = 0; i < length; i++) {
                const e = envelope[half + i];
                left[i] = e > 1e-11 ? outL[half + i] / e : 0;
                right[i] = e > 1e-11 ? outR[half + i] / e : 0;
            }
            return [left, right];
        },
    };
}

/**
 * An in-place complex FFT for any length whose factors are 2, 3, 4 and 5: Stockham autosort, one
 * pass per factor, twiddles precomputed. forward is unscaled; inverse divides by n.
 */
export function createFft(n) {
    const factors = [];
    let rest = n;
    for (const radix of [4, 2, 3, 5]) {
        while (rest % radix === 0) {
            factors.push(radix);
            rest /= radix;
        }
    }
    if (rest !== 1) {
        throw new Error(`FFT length ${n} has a factor other than 2, 3 and 5`);
    }
    const cos = new Float64Array(n);
    const sin = new Float64Array(n);
    for (let k = 0; k < n; k++) {
        cos[k] = Math.cos((2 * Math.PI * k) / n);
        sin[k] = -Math.sin((2 * Math.PI * k) / n);
    }
    const workRe = new Float64Array(n);
    const workIm = new Float64Array(n);
    const aRe = new Float64Array(5);
    const aIm = new Float64Array(5);
    const bRe = new Float64Array(5);
    const bIm = new Float64Array(5);

    const transform = (re, im) => {
        let xRe = re;
        let xIm = im;
        let yRe = workRe;
        let yIm = workIm;
        let length = n;
        let stride = 1;
        for (const p of factors) {
            const m = length / p;
            const twiddleStep = n / length;
            for (let q = 0; q < m; q++) {
                for (let k = 0; k < stride; k++) {
                    // Decimation in frequency: the p-point DFT of a_j = x[k + s(q + jm)], then output r
                    // twiddled by w^(qr), w = e^(-2 pi i / length), landing at y[k + s(pq + r)].
                    for (let j = 0; j < p; j++) {
                        const from = k + (stride * (q + (j * m)));
                        aRe[j] = xRe[from];
                        aIm[j] = xIm[from];
                    }
                    if (p === 2) {
                        bRe[0] = aRe[0] + aRe[1];
                        bIm[0] = aIm[0] + aIm[1];
                        bRe[1] = aRe[0] - aRe[1];
                        bIm[1] = aIm[0] - aIm[1];
                    } else if (p === 4) {
                        const sRe = aRe[0] + aRe[2];
                        const sIm = aIm[0] + aIm[2];
                        const dRe = aRe[0] - aRe[2];
                        const dIm = aIm[0] - aIm[2];
                        const tRe = aRe[1] + aRe[3];
                        const tIm = aIm[1] + aIm[3];
                        // (a1 - a3) * -i
                        const uRe = aIm[1] - aIm[3];
                        const uIm = aRe[3] - aRe[1];
                        bRe[0] = sRe + tRe;
                        bIm[0] = sIm + tIm;
                        bRe[1] = dRe + uRe;
                        bIm[1] = dIm + uIm;
                        bRe[2] = sRe - tRe;
                        bIm[2] = sIm - tIm;
                        bRe[3] = dRe - uRe;
                        bIm[3] = dIm - uIm;
                    } else {
                        // Small odd radix: the p-point DFT written out, w_p = e^(-2 pi i / p).
                        const unit = n / p;
                        for (let r = 0; r < p; r++) {
                            let sumRe = 0;
                            let sumIm = 0;
                            for (let j = 0; j < p; j++) {
                                const w = ((j * r) % p) * unit;
                                sumRe += (aRe[j] * cos[w]) - (aIm[j] * sin[w]);
                                sumIm += (aRe[j] * sin[w]) + (aIm[j] * cos[w]);
                            }
                            bRe[r] = sumRe;
                            bIm[r] = sumIm;
                        }
                    }
                    const to = k + (stride * p * q);
                    for (let r = 0; r < p; r++) {
                        const w = (q * r * twiddleStep) % n;
                        yRe[to + (r * stride)] = (bRe[r] * cos[w]) - (bIm[r] * sin[w]);
                        yIm[to + (r * stride)] = (bRe[r] * sin[w]) + (bIm[r] * cos[w]);
                    }
                }
            }
            [xRe, yRe] = [yRe, xRe];
            [xIm, yIm] = [yIm, xIm];
            length = m;
            stride *= p;
        }
        if (xRe !== re) {
            re.set(xRe);
            im.set(xIm);
        }
    };

    return {
        forward: transform,
        inverse(re, im) {
            for (let k = 0; k < n; k++) {
                im[k] = -im[k];
            }
            transform(re, im);
            for (let k = 0; k < n; k++) {
                re[k] /= n;
                im[k] = -im[k] / n;
            }
        },
    };
}
