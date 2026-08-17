namespace PoMode.API.Features.Audio;

/// <summary>A tempo estimate in beats per minute with a confidence score normalised into [0,1].</summary>
public sealed record TempoEstimate(double Bpm, double Confidence);

/// <summary>
/// Deterministic tempo estimation via onset-envelope autocorrelation — no model, no network.
/// Frames the (mono) signal, tracks frame-to-frame energy rises (onsets), detrends the
/// resulting envelope, then autocorrelates it over the lags that correspond to the requested
/// BPM band.
///
/// Two corrections keep the peak pick on the true tempo instead of an octave of it:
/// 1. A 3-tap smoothing pass over the per-lag autocorrelation. The true beat period is rarely an
///    exact multiple of the hop size, so its onset energy splits across two neighbouring lag
///    bins instead of landing in one, while a lag near double the period recombines that split
///    energy into a single bin and can outscore either true-period bin alone. Smoothing
///    re-consolidates the split energy before comparison.
/// 2. A weighting toward the middle of the requested band, in log-BPM space, as a secondary
///    guard against genuine harmonic/sub-harmonic ambiguity once energy is no longer split.
///
/// Once the best integer lag is chosen, a parabolic interpolation over the raw (unsmoothed)
/// autocorrelation at that lag and its two neighbours refines it to a fractional lag — the
/// standard sub-bin-precision fix for a true period that does not land on an integer lag.
/// </summary>
public static class TempoEstimator
{
    private const int WindowSize = 1024;
    private const int HopSize = 512;
    private const int MovingAverageWindow = 100;

    private static readonly TempoEstimate Fallback = new(120.0, 0.0);

    public static TempoEstimate Estimate(AudioBuffer buffer, double minBpm = 60, double maxBpm = 200)
    {
        var mono = AudioDecoder.ToMono(buffer);
        var samples = mono.Samples;
        var sampleRate = mono.SampleRate;

        var frameCount = samples.Length >= WindowSize ? ((samples.Length - WindowSize) / HopSize) + 1 : 0;
        if (frameCount < 2)
        {
            return Fallback;
        }

        var energy = ComputeFrameEnergy(samples, frameCount);
        var detrended = BuildDetrendedOnsetEnvelope(energy, frameCount);

        var totalEnvelopeEnergy = 0.0;
        foreach (var value in detrended)
        {
            totalEnvelopeEnergy += Math.Abs(value);
        }
        if (totalEnvelopeEnergy < 1e-9)
        {
            return Fallback;
        }

        var frameRate = sampleRate / (double)HopSize;
        var lagMin = Math.Max(1, (int)Math.Round(frameRate * 60.0 / maxBpm));
        var lagMax = Math.Min(frameCount - 1, (int)Math.Round(frameRate * 60.0 / minBpm));
        if (lagMax <= lagMin)
        {
            return Fallback;
        }

        var (bestLag, rawScores, smoothedScores, paddedMin) =
            FindPeakLag(detrended, frameCount, frameRate, lagMin, lagMax, minBpm, maxBpm);

        var refinedLag = RefinePeakLag(bestLag, rawScores, paddedMin);

        var meanAbsScore = 0.0;
        foreach (var value in smoothedScores)
        {
            meanAbsScore += Math.Abs(value);
        }
        meanAbsScore /= smoothedScores.Length;

        var peakScore = smoothedScores[bestLag - lagMin];
        var ratio = meanAbsScore != 0 ? peakScore / meanAbsScore : 0.0;
        if (double.IsNaN(ratio) || double.IsInfinity(ratio))
        {
            ratio = 0.0;
        }
        var confidence = Math.Clamp((ratio - 1.0) / 2.0, 0.0, 1.0);

        var estimatedBpm = Math.Clamp(frameRate * 60.0 / refinedLag, minBpm, maxBpm);
        return new TempoEstimate(estimatedBpm, confidence);
    }

    private static double[] ComputeFrameEnergy(float[] samples, int frameCount)
    {
        var energy = new double[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var start = frame * HopSize;
            var sum = 0.0;
            for (var i = 0; i < WindowSize; i++)
            {
                var s = samples[start + i];
                sum += (double)s * s;
            }
            energy[frame] = sum;
        }
        return energy;
    }

    /// <summary>Half-wave-rectified frame-to-frame energy rise, with a trailing 100-frame moving average removed.</summary>
    private static double[] BuildDetrendedOnsetEnvelope(double[] energy, int frameCount)
    {
        var onset = new double[frameCount];
        for (var frame = 1; frame < frameCount; frame++)
        {
            onset[frame] = Math.Max(0.0, energy[frame] - energy[frame - 1]);
        }

        var detrended = new double[frameCount];
        var windowSum = 0.0;
        for (var frame = 0; frame < frameCount; frame++)
        {
            windowSum += onset[frame];
            if (frame >= MovingAverageWindow)
            {
                windowSum -= onset[frame - MovingAverageWindow];
            }
            var windowLength = Math.Min(frame + 1, MovingAverageWindow);
            detrended[frame] = onset[frame] - (windowSum / windowLength);
        }
        return detrended;
    }

    private static (int BestLag, double[] RawScores, double[] SmoothedScores, int PaddedMin) FindPeakLag(
        double[] detrended, int frameCount, double frameRate, int lagMin, int lagMax, double minBpm, double maxBpm)
    {
        var paddedMin = Math.Max(1, lagMin - 2);
        var paddedMax = Math.Min(frameCount - 1, lagMax + 2);
        var raw = new double[paddedMax - paddedMin + 1];
        for (var lag = paddedMin; lag <= paddedMax; lag++)
        {
            var terms = frameCount - lag;
            var sum = 0.0;
            for (var i = 0; i < terms; i++)
            {
                sum += detrended[i] * detrended[i + lag];
            }
            raw[lag - paddedMin] = sum / terms;
        }

        var centerLogBpm = Math.Log(Math.Sqrt(minBpm * maxBpm));
        var sigma = Math.Log(maxBpm / minBpm) / 2.0;

        var smoothed = new double[lagMax - lagMin + 1];
        var bestLag = lagMin;
        var bestWeightedScore = double.NegativeInfinity;

        for (var lag = lagMin; lag <= lagMax; lag++)
        {
            var rawIndex = lag - paddedMin;
            var loIndex = Math.Max(0, rawIndex - 1);
            var hiIndex = Math.Min(raw.Length - 1, rawIndex + 1);
            var score = 0.0;
            for (var i = loIndex; i <= hiIndex; i++)
            {
                score += raw[i];
            }
            score /= hiIndex - loIndex + 1;
            smoothed[lag - lagMin] = score;

            var bpm = frameRate * 60.0 / lag;
            var logDiff = (Math.Log(bpm) - centerLogBpm) / sigma;
            var weighted = score * Math.Exp(-0.5 * logDiff * logDiff);

            if (weighted > bestWeightedScore)
            {
                bestWeightedScore = weighted;
                bestLag = lag;
            }
        }

        return (bestLag, raw, smoothed, paddedMin);
    }

    /// <summary>
    /// Parabolic interpolation gives a fractional lag when the true period falls between two
    /// integer lag bins, but it only makes sense centred on an actual local maximum of the raw
    /// autocorrelation. <paramref name="bestLag"/> comes from the smoothed, weighted score (to
    /// dodge octave errors) and is not always that local maximum itself, so the true peak is
    /// first located among <paramref name="bestLag"/> and its immediate neighbours before
    /// interpolating around it. Falls back to an untouched integer lag when a neighbour is
    /// unavailable or the three points do not form a usable parabola.
    /// </summary>
    private static double RefinePeakLag(int bestLag, double[] rawScores, int paddedMin)
    {
        var bestLagIndex = bestLag - paddedMin;
        var peakIndex = bestLagIndex;
        for (var i = bestLagIndex - 1; i <= bestLagIndex + 1; i++)
        {
            if (i >= 0 && i < rawScores.Length && rawScores[i] > rawScores[peakIndex])
            {
                peakIndex = i;
            }
        }

        if (peakIndex - 1 < 0 || peakIndex + 1 >= rawScores.Length)
        {
            return peakIndex + paddedMin;
        }

        var left = rawScores[peakIndex - 1];
        var mid = rawScores[peakIndex];
        var right = rawScores[peakIndex + 1];

        var denominator = left - (2 * mid) + right;
        if (Math.Abs(denominator) < 1e-12)
        {
            return peakIndex + paddedMin;
        }

        var offset = Math.Clamp(0.5 * (left - right) / denominator, -1.0, 1.0);
        return peakIndex + paddedMin + offset;
    }
}
