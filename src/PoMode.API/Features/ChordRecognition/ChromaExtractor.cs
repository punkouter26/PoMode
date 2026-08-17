using System.Numerics;
using PoMode.API.Features.Audio;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>A sequence of 12-bin chroma vectors sampled at a fixed hop rate.</summary>
public sealed record ChromaGram(float[][] Frames, double FramesPerSecond);

/// <summary>
/// Extracts pitch-class ("chroma") energy vectors from audio: Hann-windowed FFT magnitude spectra
/// folded into 12 semitone bins (one per pitch class), L2-normalised.
///
/// <para>§13.6 fix (a): the original 4096-sample window at 44.1 kHz gave ~10.8 Hz bin spacing,
/// which cannot separate adjacent semitones in the bass register — the measured cause of the
/// A#/A#m ghost chords on the real-track check. Two changes address it: <see cref="Compute"/>
/// resamples to 22.05 kHz and uses an 8192-sample window (~2.7 Hz spacing, ~4× finer), and
/// <see cref="Frame"/> maps each FFT bin into log-frequency (semitone) space with a triangular
/// split between the two nearest semitones instead of hard-rounding a smeared bin into one
/// pitch class.</para>
/// </summary>
public static class ChromaExtractor
{
    private const int PitchClasses = 12;
    private const double MinFrequencyHz = 55.0; // below A1; excludes rumble/DC from skewing the chroma

    /// <summary>
    /// Upper limit of the gathered band. Above ~2 kHz the spectrum is dominated by high harmonics
    /// (5th, 7th…) that land on non-chord pitch classes and by percussive noise; chord identity
    /// lives in the fundamentals and low harmonics below it.
    /// </summary>
    private const double MaxFrequencyHz = 2000.0;

    /// <summary>The chromagram's fixed analysis rate — also §13.6's named resample target.</summary>
    public const int TargetSampleRate = 22050;

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
            if (frequency < MinFrequencyHz || frequency > MaxFrequencyHz || frequency >= nyquist)
            {
                continue;
            }

            // Log-frequency (constant-Q-style) mapping: place the bin at its fractional semitone
            // position and split its magnitude linearly between the two neighbouring semitones. A
            // bin sitting between two semitones then contributes to both instead of being rounded
            // wholesale into whichever is nearer — the rounding was what turned bass-register
            // spectral smear into confident wrong pitch classes.
            var midi = 69 + (12 * Math.Log2(frequency / 440.0));
            var lower = (int)Math.Floor(midi);
            var fraction = midi - lower;
            var magnitude = buffer[bin].Magnitude;
            chroma[PitchClass(lower)] += magnitude * (1 - fraction);
            chroma[PitchClass(lower + 1)] += magnitude * fraction;
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

    private static int PitchClass(int midi) => ((midi % PitchClasses) + PitchClasses) % PitchClasses;

    /// <summary>
    /// Computes a chroma vector every <paramref name="hopSize"/> samples across the whole buffer,
    /// after downmixing to mono and resampling to <see cref="TargetSampleRate"/>. The 8192-sample
    /// default window at 22.05 kHz spans ~371 ms — long enough to resolve bass semitones
    /// (~2.7 Hz bins), still well under a beat at any plausible tempo.
    /// </summary>
    public static ChromaGram Compute(AudioBuffer buffer, int windowSize = 8192, int hopSize = 2048)
    {
        if (windowSize <= 0 || (windowSize & (windowSize - 1)) != 0)
        {
            throw new ArgumentException("windowSize must be a power of two.", nameof(windowSize));
        }

        var mono = AudioDecoder.ToMono(buffer);
        if (mono.SampleRate != TargetSampleRate)
        {
            mono = AudioDecoder.Resample(mono, TargetSampleRate);
        }
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
