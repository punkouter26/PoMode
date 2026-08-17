using System.Numerics;
using PoMode.API.Features.Audio;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>A sequence of 12-bin chroma vectors sampled at a fixed hop rate.</summary>
public sealed record ChromaGram(float[][] Frames, double FramesPerSecond);

/// <summary>
/// Extracts pitch-class ("chroma") energy vectors from audio: Hann-windowed FFT magnitude spectra
/// folded into 12 semitone bins (one per pitch class), L2-normalised.
/// </summary>
public static class ChromaExtractor
{
    private const int PitchClasses = 12;
    private const double MinFrequencyHz = 55.0; // below A1; excludes rumble/DC from skewing the chroma

    /// <summary>Computes a chroma vector for one frame of samples. All-zero input yields an all-zero vector.</summary>
    public static float[] Frame(ReadOnlySpan<float> samples, int sampleRate)
    {
        var n = samples.Length;
        var buffer = new Complex[n];
        for (var i = 0; i < n; i++)
        {
            // Hann window.
            var window = n > 1 ? 0.5 - (0.5 * Math.Cos(2 * Math.PI * i / (n - 1))) : 1.0;
            buffer[i] = new Complex(samples[i] * window, 0);
        }

        Fft.Transform(buffer);

        var chroma = new double[PitchClasses];
        var nyquist = sampleRate / 2.0;
        var halfN = n / 2;
        for (var bin = 1; bin < halfN; bin++)
        {
            var frequency = bin * sampleRate / (double)n;
            if (frequency < MinFrequencyHz || frequency >= nyquist)
            {
                continue;
            }

            var pitchClass = (((int)Math.Round(69 + (12 * Math.Log2(frequency / 440.0))) % PitchClasses) + PitchClasses) % PitchClasses;
            chroma[pitchClass] += buffer[bin].Magnitude;
        }

        var norm = Math.Sqrt(chroma.Sum(v => v * v));
        var result = new float[PitchClasses];
        if (norm > 0)
        {
            for (var i = 0; i < PitchClasses; i++)
            {
                result[i] = (float)(chroma[i] / norm);
            }
        }
        return result;
    }

    /// <summary>Computes a chroma vector every <paramref name="hopSize"/> samples across the whole buffer.</summary>
    public static ChromaGram Compute(AudioBuffer buffer, int windowSize = 4096, int hopSize = 2048)
    {
        if (windowSize <= 0 || (windowSize & (windowSize - 1)) != 0)
        {
            throw new ArgumentException("windowSize must be a power of two.", nameof(windowSize));
        }

        var mono = AudioDecoder.ToMono(buffer);
        var samples = mono.Samples;

        var frames = new List<float[]>();
        var windowed = new float[windowSize];
        for (var start = 0; start < samples.Length; start += hopSize)
        {
            var available = Math.Min(windowSize, samples.Length - start);
            Array.Copy(samples, start, windowed, 0, available);
            if (available < windowSize)
            {
                Array.Clear(windowed, available, windowSize - available);
            }
            frames.Add(Frame(windowed, mono.SampleRate));
        }

        var framesPerSecond = mono.SampleRate / (double)hopSize;
        return new ChromaGram([.. frames], framesPerSecond);
    }
}
