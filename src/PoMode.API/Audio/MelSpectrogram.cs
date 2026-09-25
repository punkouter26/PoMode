using System.Numerics;

namespace PoMode.API.Audio;

/// <summary>
/// The spectrogram front ends the neural executors expect, reproduced exactly from their reference
/// implementations: <c>torch.stft</c> with <c>center=True</c> (reflect padding) and a periodic Hann
/// window, projected onto a mel filterbank. A model is only as good as the features it is fed, and
/// every one of these details (window, padding, normalisation) moves the output, so they are fixed
/// here rather than approximated per caller.
/// </summary>
public static class MelSpectrogram
{
    /// <summary>
    /// Magnitude STFT, one row of <c>nFft/2+1</c> bins per frame, frame count <c>1 + len/hop</c> —
    /// the <c>torch.stft(center=True, pad_mode="reflect")</c> layout. <paramref name="scale"/>
    /// multiplies every magnitude (Beat This! uses <c>1/sqrt(nFft)</c>, torchaudio's
    /// <c>normalized="frame_length"</c>).
    /// </summary>
    public static float[][] StftMagnitudes(float[] samples, int nFft, int hop, double scale = 1.0)
    {
        var half = nFft / 2;
        var frameCount = 1 + (samples.Length / hop);
        var bins = half + 1;
        var window = new double[nFft];
        for (var n = 0; n < nFft; n++)
        {
            window[n] = 0.5 - (0.5 * Math.Cos(2 * Math.PI * n / nFft)); // periodic Hann
        }

        var frames = new float[frameCount][];
        Parallel.For(0, frameCount, () => new Complex[nFft], (frame, _, buffer) =>
        {
            var start = (frame * hop) - half;
            for (var n = 0; n < nFft; n++)
            {
                buffer[n] = new Complex(Reflect(samples, start + n) * window[n], 0);
            }
            Fft.Transform(buffer);
            var row = new float[bins];
            for (var k = 0; k < bins; k++)
            {
                row[k] = (float)(buffer[k].Magnitude * scale);
            }
            frames[frame] = row;
            return buffer;
        }, _ => { });
        return frames;
    }

    /// <summary>
    /// <c>librosa.filters.mel(htk=True)</c> with its default Slaney area normalisation — the
    /// filterbank RMVPE was trained on. Row-major <c>[nMels][nFft/2+1]</c>.
    /// </summary>
    public static float[][] HtkFilterbank(int sampleRate, int nFft, int nMels, double fMin, double fMax)
    {
        static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + (hz / 700.0));
        static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

        var bins = (nFft / 2) + 1;
        var fftFreqs = new double[bins];
        for (var k = 0; k < bins; k++)
        {
            fftFreqs[k] = k * (sampleRate / 2.0) / (bins - 1);
        }

        var melMin = HzToMel(fMin);
        var melMax = HzToMel(fMax);
        var melFreqs = new double[nMels + 2];
        for (var i = 0; i < melFreqs.Length; i++)
        {
            melFreqs[i] = MelToHz(melMin + ((melMax - melMin) * i / (nMels + 1)));
        }

        var filters = new float[nMels][];
        for (var m = 0; m < nMels; m++)
        {
            var lowerWidth = melFreqs[m + 1] - melFreqs[m];
            var upperWidth = melFreqs[m + 2] - melFreqs[m + 1];
            var norm = 2.0 / (melFreqs[m + 2] - melFreqs[m]);
            var row = new float[bins];
            for (var k = 0; k < bins; k++)
            {
                var lower = (fftFreqs[k] - melFreqs[m]) / lowerWidth;
                var upper = (melFreqs[m + 2] - fftFreqs[k]) / upperWidth;
                row[k] = (float)(Math.Max(0.0, Math.Min(lower, upper)) * norm);
            }
            filters[m] = row;
        }
        return filters;
    }

    /// <summary>Dot product of one magnitude frame with one filter row.</summary>
    public static float Project(float[] magnitudes, float[] filter)
    {
        var sum = 0f;
        for (var k = 0; k < magnitudes.Length; k++)
        {
            sum += magnitudes[k] * filter[k];
        }
        return sum;
    }

    /// <summary>numpy/torch "reflect" padding (edge sample not repeated); zero past a clip too short to reflect.</summary>
    private static double Reflect(float[] samples, int index)
    {
        var length = samples.Length;
        if (length == 0)
        {
            return 0;
        }
        if (index < 0)
        {
            index = -index;
        }
        if (index >= length)
        {
            index = (2 * (length - 1)) - index;
        }
        return index >= 0 && index < length ? samples[index] : 0;
    }
}
