using PoMode.API.Features.Audio;
using Xunit;

namespace PoMode.Unit.Audio;

public class TuningEstimatorTests
{
    private const int SampleRate = 22050;

    /// <summary>A sung C major scale, every note shifted by the same number of cents.</summary>
    private static AudioBuffer Scale(double cents)
    {
        int[] midis = [60, 62, 64, 65, 67, 69, 71, 72];
        const double noteSeconds = 0.5;
        var perNote = (int)(SampleRate * noteSeconds);
        var samples = new float[perNote * midis.Length];

        for (var n = 0; n < midis.Length; n++)
        {
            var frequency = 440.0 * Math.Pow(2.0, (midis[n] - 69) / 12.0) * Math.Pow(2.0, cents / 1200.0);
            for (var i = 0; i < perNote; i++)
            {
                var t = i / (double)SampleRate;
                // A fundamental with two harmonics: a bare sine is easier to track than a voice.
                var value = Math.Sin(2 * Math.PI * frequency * t)
                    + (0.4 * Math.Sin(4 * Math.PI * frequency * t))
                    + (0.2 * Math.Sin(6 * Math.PI * frequency * t));
                samples[(n * perNote) + i] = (float)(0.3 * value);
            }
        }
        return new AudioBuffer(samples, SampleRate, 1);
    }

    private static AudioBuffer Noise()
    {
        var random = new Random(7);
        var samples = new float[SampleRate * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2) - 1) * 0.3f;
        }
        return new AudioBuffer(samples, SampleRate, 1);
    }

    /// <summary>
    /// One offset shared by every note is measurable and gets applied; deviations that do not agree
    /// leave the recording alone. Both halves matter: correcting a recording that has no single
    /// offset would move notes that were never uniformly wrong.
    /// </summary>
    [Fact]
    public void A_shared_detuning_is_measured_and_a_scattered_one_is_left_alone()
    {
        Assert.InRange(TuningEstimator.Estimate(Scale(0)).AppliedCents, -2.0, 2.0);

        // The case the Analyze page reports: a voice sitting a few cents sharp throughout.
        Assert.InRange(TuningEstimator.Estimate(Scale(6)).AppliedCents, 4.0, 8.0);

        Assert.InRange(TuningEstimator.Estimate(Scale(-20)).AppliedCents, -22.0, -18.0);
        Assert.InRange(TuningEstimator.Estimate(Scale(45)).AppliedCents, 43.0, 47.0);

        // Noise has no tuning, so nothing is applied however the raw angle happens to land.
        Assert.Equal(0.0, TuningEstimator.Estimate(Noise()).AppliedCents);
    }
}
