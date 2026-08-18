namespace PoMode.API.Features.Audio;

/// <summary>A tempo estimate in beats per minute with a confidence score normalised into [0,1].</summary>
public sealed record TempoEstimate(double Bpm, double Confidence);

/// <summary>
/// A regular beat grid: beats fall at <see cref="FirstBeatSec"/> + k·(60/<see cref="Bpm"/>).
/// <see cref="Confidence"/> is the underlying tempo estimate's confidence — a caller should treat
/// a low-confidence grid as "no usable beats" rather than snapping anything to it.
/// </summary>
public sealed record BeatGrid(double Bpm, double FirstBeatSec, double Confidence);

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
/// 2. An explicit harmonic check: once the smoothed-score peak lag L is chosen, the score at
///    approximately L/2 (the faster, double-tempo candidate) is compared to the score at L. If
///    it is close — see <see cref="FasterOctaveScoreRatio"/> for the exact threshold and why —
///    the faster octave is preferred. An earlier version of this weighted lags toward the middle
///    of the band in log-BPM space instead; that biased slow octaves for any true tempo above
///    roughly sqrt(2) times the band's geometric-mean centre (≈155 BPM in the default 60-200
///    band), silently halving fast tempos. The harmonic check replaces that band-position guess
///    with a direct comparison of the two candidates actually in competition.
///
/// Once the final integer lag is chosen, a parabolic interpolation over the raw (unsmoothed)
/// autocorrelation at that lag and its two neighbours refines it to a fractional lag — the
/// standard sub-bin-precision fix for a true period that does not land on an integer lag.
/// </summary>
public static class TempoEstimator
{
    private const int WindowSize = 1024;
    private const int HopSize = 512;
    private const int MovingAverageWindow = 100;

    /// <summary>
    /// How close the faster (double-tempo) candidate's smoothed score must be to the winning
    /// lag's score, as a fraction, before the faster octave is preferred.
    ///
    /// When a lag wins only because it recombined onset energy split across two true-period bins
    /// (see the smoothing note above), the observed boost is large — roughly 1.5-2x the genuine
    /// candidate's score in this implementation's own click-track measurements. A real ambiguity
    /// between two candidates that both plausibly explain the onset pattern, by contrast, scores
    /// within a much narrower margin. 0.8 (within 20%) sits at the permissive edge of that gap:
    /// comfortably below the ~1.5-2x recombination boost, so a genuinely spurious slow octave is
    /// never mistaken for a close call, while still catching the closer ties that come from
    /// choosing the faster candidate whenever it credibly explains the same onsets.
    /// </summary>
    private const double FasterOctaveScoreRatio = 0.8;

    private static readonly TempoEstimate Fallback = new(120.0, 0.0);

    /// <summary>The shared per-buffer front end: mono mix, frame energy, detrended onset envelope.
    /// Null when the buffer is too short to frame. Computed once and reused by both the tempo
    /// search and the beat-phase search, which need the identical envelope.</summary>
    private readonly record struct OnsetEnvelope(double[] Detrended, int FrameCount, double FrameRate);

    private static OnsetEnvelope? PrepareEnvelope(AudioBuffer buffer)
    {
        var mono = AudioDecoder.ToMono(buffer);
        var samples = mono.Samples;
        var frameCount = samples.Length >= WindowSize ? ((samples.Length - WindowSize) / HopSize) + 1 : 0;
        if (frameCount < 2)
        {
            return null;
        }
        var energy = ComputeFrameEnergy(samples, frameCount);
        return new OnsetEnvelope(
            BuildDetrendedOnsetEnvelope(energy, frameCount), frameCount, mono.SampleRate / (double)HopSize);
    }

    public static TempoEstimate Estimate(AudioBuffer buffer, double minBpm = 60, double maxBpm = 200)
        => PrepareEnvelope(buffer) is { } envelope ? EstimateFromEnvelope(envelope, minBpm, maxBpm) : Fallback;

    private static TempoEstimate EstimateFromEnvelope(OnsetEnvelope envelope, double minBpm, double maxBpm)
    {
        var (detrended, frameCount, frameRate) = envelope;

        var totalEnvelopeEnergy = 0.0;
        foreach (var value in detrended)
        {
            totalEnvelopeEnergy += Math.Abs(value);
        }
        if (totalEnvelopeEnergy < 1e-9)
        {
            return Fallback;
        }

        var lagMin = Math.Max(1, (int)Math.Round(frameRate * 60.0 / maxBpm));
        var lagMax = Math.Min(frameCount - 1, (int)Math.Round(frameRate * 60.0 / minBpm));
        if (lagMax <= lagMin)
        {
            return Fallback;
        }

        var (initialBestLag, rawScores, smoothedScores, paddedMin) =
            FindPeakLag(detrended, frameCount, lagMin, lagMax);

        var bestLag = PreferFasterOctaveIfComparable(initialBestLag, smoothedScores, lagMin, lagMax);

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

    /// <summary>
    /// Extends <see cref="Estimate"/> with a beat *phase*: the offset (within one period) whose
    /// beat-spaced comb best aligns with the detrended onset envelope. Autocorrelation alone gives
    /// the period but not where beats fall; §13.6's beat-synchronous chord segmentation needs both.
    /// When the tempo estimate itself has zero confidence the phase is meaningless and 0 is
    /// returned — callers gate on <see cref="BeatGrid.Confidence"/>.
    /// </summary>
    public static BeatGrid EstimateGrid(AudioBuffer buffer, double minBpm = 60, double maxBpm = 200)
    {
        if (PrepareEnvelope(buffer) is not { } envelope)
        {
            return new BeatGrid(Fallback.Bpm, 0.0, Fallback.Confidence);
        }
        var estimate = EstimateFromEnvelope(envelope, minBpm, maxBpm);
        if (estimate.Confidence <= 0)
        {
            return new BeatGrid(estimate.Bpm, 0.0, estimate.Confidence);
        }

        var (detrended, frameCount, frameRate) = envelope;
        var periodFrames = frameRate * 60.0 / estimate.Bpm;
        var offsetCount = Math.Max(1, (int)Math.Floor(periodFrames));

        var bestOffset = 0;
        var bestScore = double.NegativeInfinity;
        for (var offset = 0; offset < offsetCount; offset++)
        {
            var sum = 0.0;
            var count = 0;
            // Fractional stepping, so a period that is not an integer frame count cannot drift.
            for (var beat = offset + 0.0; ; beat += periodFrames)
            {
                var frame = (int)Math.Round(beat);
                if (frame >= frameCount)
                {
                    break;
                }
                sum += detrended[frame];
                count++;
            }
            var score = count > 0 ? sum / count : double.NegativeInfinity;
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }

        return new BeatGrid(estimate.Bpm, bestOffset / frameRate, estimate.Confidence);
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
        double[] detrended, int frameCount, int lagMin, int lagMax)
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

        var smoothed = new double[lagMax - lagMin + 1];
        var bestLag = lagMin;
        var bestScore = double.NegativeInfinity;

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

            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        return (bestLag, raw, smoothed, paddedMin);
    }

    /// <summary>
    /// Checks the score at integer divisors of the winning lag — half, a third, a quarter, and so
    /// on (i.e. double, triple, quadruple the winning tempo) — for as long as the divided lag
    /// stays inside the band. Whichever of those candidates is both fastest and within
    /// <see cref="FasterOctaveScoreRatio"/> of the original winner's score is preferred, since it
    /// credibly explains the same onsets at a higher tempo.
    ///
    /// A single halving check (divisor 2 only) fixes the ordinary octave error, but the same
    /// recombination effect that causes it can compound: very short true periods near the top of
    /// the band leave little room for their energy to land cleanly in one lag bin, so the winning
    /// lag can land on triple (or higher) the true period instead of double it. Walking the
    /// divisors — always compared against the one, original winner score, not chained from one
    /// candidate to the next — generalises the same check to that case without introducing a new
    /// threshold or any tempo-specific casing.
    /// </summary>
    private static int PreferFasterOctaveIfComparable(int bestLag, double[] smoothedScores, int lagMin, int lagMax)
    {
        var winnerScore = smoothedScores[bestLag - lagMin];
        if (winnerScore <= 0)
        {
            return bestLag;
        }

        var chosen = bestLag;
        var previousCandidateLag = bestLag;
        for (var divisor = 2; ; divisor++)
        {
            var candidateLag = (int)Math.Round(bestLag / (double)divisor);
            // Each divisor must yield a strictly smaller lag than the last one tried; integer
            // division plateaus (e.g. bestLag=3 gives lag 1 for every divisor >= 3), so this is
            // what guarantees the loop terminates instead of spinning on a repeated candidate.
            if (candidateLag < lagMin || candidateLag >= previousCandidateLag)
            {
                break;
            }
            previousCandidateLag = candidateLag;

            var candidateScore = smoothedScores[candidateLag - lagMin];
            if (candidateScore >= FasterOctaveScoreRatio * winnerScore)
            {
                chosen = candidateLag;
            }
        }

        return chosen;
    }

    /// <summary>
    /// Parabolic interpolation gives a fractional lag when the true period falls between two
    /// integer lag bins, but it only makes sense centred on an actual local maximum of the raw
    /// autocorrelation. <paramref name="bestLag"/> comes from the smoothed score and the
    /// faster-octave check (to dodge octave errors) and is not always that local maximum itself,
    /// so the true peak is first located among <paramref name="bestLag"/> and its immediate
    /// neighbours before interpolating around it. Falls back to an untouched integer lag when a
    /// neighbour is unavailable or the three points do not form a usable parabola.
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
