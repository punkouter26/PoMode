namespace PoMode.API.Features.Separation;

/// <summary>
/// MDX-Net vocal separation, the signal processing around the model: the C# twin of the browser's
/// <c>js/infer/mdx.js</c>, itself a port of Ultimate Vocal Remover's reference <c>demix</c> for
/// UVR-MDX-NET-Voc_FT. torch.stft/istft semantics (periodic Hann, centred, reflect-padded), UVR's
/// chunking with a symmetric Hann cross-fade, the three lowest bins zeroed on the way in, the model's
/// output scaled by its compensation factor. The model call is injected, so this is plain arithmetic
/// the unit tests run without a model.
/// </summary>
/// <remarks>
/// The FFT length, 7680, is 2^9 x 3 x 5, hence the small mixed-radix Stockham FFT. Both channels go
/// through one complex FFT (left + i*right) and are pulled apart by symmetry, which halves the work
/// of the STFT and the ISTFT alike. Kept line for line with mdx.js so a fix to one is a fix to both.
/// </remarks>
public static class MdxDemix
{
    public const int NFft = 7680;
    public const int Hop = 1024;
    public const int DimF = 3072;
    public const int DimT = 256;
    public const double Compensate = 1.021;
    public const int SampleRate = 44100;

    /// <summary>
    /// Separates the vocals from a stereo mix at 44.1 kHz. <paramref name="runModel"/> takes the
    /// spectrogram laid out [4, DimF, DimT] (left re, left im, right re, right im) and returns the
    /// model's output in the same layout.
    /// </summary>
    public static (float[] Left, float[] Right) SeparateVocals(
        float[] left, float[] right, Func<float[], float[]> runModel, Action<double>? onProgress = null, CancellationToken ct = default)
    {
        var chunk = Hop * (DimT - 1);
        var trim = NFft / 2;
        var generated = chunk - (2 * trim);
        var length = left.Length;
        var total = trim + length + generated + trim - (length % generated);

        var mixL = new float[total];
        var mixR = new float[total];
        Array.Copy(left, 0, mixL, trim, length);
        Array.Copy(right, 0, mixR, trim, length);

        var outL = new double[total];
        var outR = new double[total];
        var divider = new double[total];
        var stft = new Stft();
        var step = chunk - NFft;
        var starts = new List<int>();
        for (var start = 0; start < total; start += step)
        {
            starts.Add(start);
        }

        var partL = new float[chunk];
        var partR = new float[chunk];
        for (var index = 0; index < starts.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var start = starts[index];
            var end = Math.Min(start + chunk, total);
            var actual = end - start;
            Array.Clear(partL);
            Array.Clear(partR);
            Array.Copy(mixL, start, partL, 0, actual);
            Array.Copy(mixR, start, partR, 0, actual);

            var (vocalL, vocalR) = stft.Inverse(runModel(stft.Forward(partL, partR)));

            // np.hanning: the symmetric window, over the part of the chunk that is real audio.
            for (var i = 0; i < actual; i++)
            {
                var w = actual > 1 ? 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (actual - 1))) : 1;
                outL[start + i] += vocalL[i] * w;
                outR[start + i] += vocalR[i] * w;
                divider[start + i] += w;
            }
            onProgress?.Invoke((index + 1) / (double)starts.Count);
        }

        var vocalsL = new float[length];
        var vocalsR = new float[length];
        for (var i = 0; i < length; i++)
        {
            var d = divider[trim + i];
            vocalsL[i] = d > 0 ? (float)(outL[trim + i] / d * Compensate) : 0;
            vocalsR[i] = d > 0 ? (float)(outR[trim + i] / d * Compensate) : 0;
        }
        return (vocalsL, vocalsR);
    }

    /// <summary>torch.stft / torch.istft for one MDX chunk, both channels at once.</summary>
    public sealed class Stft
    {
        private const int Half = NFft / 2;
        private const int Length = Hop * (DimT - 1);
        private readonly MixedRadixFft _fft = new(NFft);
        private readonly double[] _window = new double[NFft];
        private readonly double[] _re = new double[NFft];
        private readonly double[] _im = new double[NFft];

        public Stft()
        {
            for (var n = 0; n < NFft; n++)
            {
                _window[n] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * n / NFft)); // periodic Hann
            }
        }

        private static float Reflect(float[] signal, int index)
            => index < 0 ? signal[-index] : index >= Length ? signal[(2 * Length) - 2 - index] : signal[index];

        /// <summary>[4, DimF, DimT] from two channels of exactly one chunk; the lowest three bins zeroed.</summary>
        public float[] Forward(float[] left, float[] right)
        {
            var spec = new float[4 * DimF * DimT];
            const int plane = DimF * DimT;
            for (var t = 0; t < DimT; t++)
            {
                var origin = (t * Hop) - Half;
                for (var n = 0; n < NFft; n++)
                {
                    _re[n] = Reflect(left, origin + n) * _window[n];
                    _im[n] = Reflect(right, origin + n) * _window[n];
                }
                _fft.Forward(_re, _im);
                for (var f = 3; f < DimF; f++)
                {
                    var g = (NFft - f) % NFft;
                    var at = (f * DimT) + t;
                    // Z = L + iR: L[f] = (Z[f] + conj Z[-f]) / 2, R[f] = (Z[f] - conj Z[-f]) / 2i.
                    spec[at] = (float)((_re[f] + _re[g]) / 2);
                    spec[plane + at] = (float)((_im[f] - _im[g]) / 2);
                    spec[(2 * plane) + at] = (float)((_im[f] + _im[g]) / 2);
                    spec[(3 * plane) + at] = (float)((_re[g] - _re[f]) / 2);
                }
            }
            return spec;
        }

        /// <summary>Two channels of one chunk from [4, DimF, DimT]; bins above DimF are zero, as UVR pads them.</summary>
        public (float[] Left, float[] Right) Inverse(float[] spec)
        {
            const int plane = DimF * DimT;
            var outL = new double[Length + NFft];
            var outR = new double[Length + NFft];
            var envelope = new double[Length + NFft];
            for (var t = 0; t < DimT; t++)
            {
                Array.Clear(_re);
                Array.Clear(_im);
                for (var f = 0; f < DimF; f++)
                {
                    var at = (f * DimT) + t;
                    // irfft discards the imaginary part of the DC bin; packed, it would leak across.
                    double lr = spec[at];
                    double li = f == 0 ? 0 : spec[plane + at];
                    double rr = spec[(2 * plane) + at];
                    double ri = f == 0 ? 0 : spec[(3 * plane) + at];
                    // Y = L + iR, with each half-spectrum's Hermitian mirror filled in.
                    _re[f] = lr - ri;
                    _im[f] = li + rr;
                    if (f > 0)
                    {
                        _re[NFft - f] = lr + ri;
                        _im[NFft - f] = rr - li;
                    }
                }
                _fft.Inverse(_re, _im);
                var origin = t * Hop;
                for (var n = 0; n < NFft; n++)
                {
                    outL[origin + n] += _re[n] * _window[n];
                    outR[origin + n] += _im[n] * _window[n];
                    envelope[origin + n] += _window[n] * _window[n];
                }
            }
            var leftOut = new float[Length];
            var rightOut = new float[Length];
            for (var i = 0; i < Length; i++)
            {
                var e = envelope[Half + i];
                leftOut[i] = e > 1e-11 ? (float)(outL[Half + i] / e) : 0;
                rightOut[i] = e > 1e-11 ? (float)(outR[Half + i] / e) : 0;
            }
            return (leftOut, rightOut);
        }
    }

    /// <summary>
    /// An in-place complex FFT for any length whose factors are 2, 3, 4 and 5: Stockham autosort, one
    /// pass per factor, twiddles precomputed. Forward is unscaled; Inverse divides by n.
    /// </summary>
    public sealed class MixedRadixFft
    {
        private readonly int _n;
        private readonly List<int> _factors = [];
        private readonly double[] _cos;
        private readonly double[] _sin;
        private readonly double[] _workRe;
        private readonly double[] _workIm;
        private readonly double[] _aRe = new double[5];
        private readonly double[] _aIm = new double[5];
        private readonly double[] _bRe = new double[5];
        private readonly double[] _bIm = new double[5];

        public MixedRadixFft(int n)
        {
            _n = n;
            var rest = n;
            foreach (var radix in (int[])[4, 2, 3, 5])
            {
                while (rest % radix == 0)
                {
                    _factors.Add(radix);
                    rest /= radix;
                }
            }
            if (rest != 1)
            {
                throw new ArgumentException($"FFT length {n} has a factor other than 2, 3 and 5.", nameof(n));
            }
            _cos = new double[n];
            _sin = new double[n];
            for (var k = 0; k < n; k++)
            {
                _cos[k] = Math.Cos(2 * Math.PI * k / n);
                _sin[k] = -Math.Sin(2 * Math.PI * k / n);
            }
            _workRe = new double[n];
            _workIm = new double[n];
        }

        public void Forward(double[] re, double[] im)
        {
            var xRe = re;
            var xIm = im;
            var yRe = _workRe;
            var yIm = _workIm;
            var length = _n;
            var stride = 1;
            foreach (var p in _factors)
            {
                var m = length / p;
                var twiddleStep = _n / length;
                for (var q = 0; q < m; q++)
                {
                    for (var k = 0; k < stride; k++)
                    {
                        // Decimation in frequency: the p-point DFT of a_j = x[k + s(q + jm)], then
                        // output r twiddled by w^(qr), landing at y[k + s(pq + r)].
                        for (var j = 0; j < p; j++)
                        {
                            var from = k + (stride * (q + (j * m)));
                            _aRe[j] = xRe[from];
                            _aIm[j] = xIm[from];
                        }
                        Butterfly(p);
                        var to = k + (stride * p * q);
                        for (var r = 0; r < p; r++)
                        {
                            var w = q * r * twiddleStep % _n;
                            yRe[to + (r * stride)] = (_bRe[r] * _cos[w]) - (_bIm[r] * _sin[w]);
                            yIm[to + (r * stride)] = (_bRe[r] * _sin[w]) + (_bIm[r] * _cos[w]);
                        }
                    }
                }
                (xRe, yRe) = (yRe, xRe);
                (xIm, yIm) = (yIm, xIm);
                length = m;
                stride *= p;
            }
            if (!ReferenceEquals(xRe, re))
            {
                Array.Copy(xRe, re, _n);
                Array.Copy(xIm, im, _n);
            }
        }

        public void Inverse(double[] re, double[] im)
        {
            for (var k = 0; k < _n; k++)
            {
                im[k] = -im[k];
            }
            Forward(re, im);
            for (var k = 0; k < _n; k++)
            {
                re[k] /= _n;
                im[k] = -im[k] / _n;
            }
        }

        private void Butterfly(int p)
        {
            if (p == 2)
            {
                _bRe[0] = _aRe[0] + _aRe[1];
                _bIm[0] = _aIm[0] + _aIm[1];
                _bRe[1] = _aRe[0] - _aRe[1];
                _bIm[1] = _aIm[0] - _aIm[1];
                return;
            }
            if (p == 4)
            {
                var sRe = _aRe[0] + _aRe[2];
                var sIm = _aIm[0] + _aIm[2];
                var dRe = _aRe[0] - _aRe[2];
                var dIm = _aIm[0] - _aIm[2];
                var tRe = _aRe[1] + _aRe[3];
                var tIm = _aIm[1] + _aIm[3];
                // (a1 - a3) * -i
                var uRe = _aIm[1] - _aIm[3];
                var uIm = _aRe[3] - _aRe[1];
                _bRe[0] = sRe + tRe;
                _bIm[0] = sIm + tIm;
                _bRe[1] = dRe + uRe;
                _bIm[1] = dIm + uIm;
                _bRe[2] = sRe - tRe;
                _bIm[2] = sIm - tIm;
                _bRe[3] = dRe - uRe;
                _bIm[3] = dIm - uIm;
                return;
            }
            // Small odd radix: the p-point DFT written out, w_p = e^(-2 pi i / p).
            var unit = _n / p;
            for (var r = 0; r < p; r++)
            {
                double sumRe = 0;
                double sumIm = 0;
                for (var j = 0; j < p; j++)
                {
                    var w = j * r % p * unit;
                    sumRe += (_aRe[j] * _cos[w]) - (_aIm[j] * _sin[w]);
                    sumIm += (_aRe[j] * _sin[w]) + (_aIm[j] * _cos[w]);
                }
                _bRe[r] = sumRe;
                _bIm[r] = sumIm;
            }
        }
    }
}
