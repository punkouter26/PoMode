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
    /// <summary>
    /// A per-measure tempo reading: the tempo the music was actually running at over that measure.
    /// </summary>
    /// <param name="Number">1-based measure number, matching the numbering the canvas shows.</param>
    /// <param name="Changed">This measure's tempo differs audibly from the previous measure's.</param>
    public sealed record TempoMeasure(int Number, double StartSec, double Bpm, bool Changed);

    /// <summary>
    /// How the tempo moves across a song. <paramref name="IsSteady"/> is the headline answer: for a
    /// programmed track it is true and the per-measure list is uninteresting, while for a human
    /// performance it is false and the list is the point.
    /// </summary>
    public sealed record TempoMap(
        double MedianBpm,
        double MinBpm,
        double MaxBpm,
        bool IsSteady,
        double Confidence,
        IReadOnlyList<TempoMeasure> Measures);

    private static readonly TempoMap EmptyMap = new(0, 0, 0, IsSteady: true, 0, []);

    /// <summary>
    /// A measure counts as a tempo change when it differs from the previous one by at least this
    /// much. Below roughly 2 BPM the difference is inside the snapping resolution — one envelope
    /// frame at 22 kHz is about 23 ms, which at 120 BPM is already ~1 BPM of apparent change — so a
    /// smaller threshold would flag measurement noise as musical rubato.
    /// </summary>
    private const double ChangeThresholdBpm = 2.0;

    /// <summary>A song whose whole range fits inside this is "steady" and needs no tempo track.</summary>
    private const double SteadyRangeBpm = 4.0;

    /// <summary>Assumed metre. The rest of the app already numbers measures in four beats.</summary>
    private const int BeatsPerMeasure = 4;

    /// <summary>
    /// Measures the tempo separately for each measure, rather than assuming one tempo for the song.
    ///
    /// <para>The method is the obvious one: take the global grid, predict where each downbeat should
    /// fall, then snap each prediction to the strongest onset near it and read the real tempo off the
    /// gap between consecutive snapped downbeats. A song that speeds up shows successive gaps
    /// shortening, which is exactly what a listener hears.</para>
    ///
    /// <para>Snapping is bounded to half a beat either side of the prediction. Without that bound a
    /// measure with no clear downbeat would latch onto whatever onset happened to be loudest nearby
    /// and report a wild tempo; with it, an unclear measure simply keeps the predicted position and
    /// reports the global tempo, which is the honest fallback.</para>
    ///
    /// <para>Returns an empty map — never a fabricated one — when the global estimate has no
    /// confidence, because every downbeat prediction would then be meaningless.</para>
    /// </summary>
    public static TempoMap EstimateTempoMap(AudioBuffer buffer, double minBpm = 60, double maxBpm = 200)
    {
        if (PrepareEnvelope(buffer) is not { } envelope)
        {
            return EmptyMap;
        }

        var estimate = EstimateFromEnvelope(envelope, minBpm, maxBpm);
        if (estimate.Confidence <= 0 || estimate.Bpm <= 0)
        {
            return EmptyMap;
        }

        var grid = EstimateGrid(buffer, minBpm, maxBpm);
        var (detrended, frameCount, frameRate) = envelope;

        var beatFrames = frameRate * 60.0 / estimate.Bpm;
        var measureFrames = beatFrames * BeatsPerMeasure;
        var searchFrames = Math.Max(1, (int)Math.Round(beatFrames / 2.0));

        // Each downbeat is predicted from the PREVIOUS one plus the most recently measured measure
        // length — not from a fixed grid laid down at the global tempo.
        //
        // Predicting from the fixed grid fails exactly where this feature is meant to work. On a
        // track accelerating 1 BPM per beat, the fixed prediction fell further behind the music every
        // measure until the error passed half a beat and the snap grabbed the *next* onset instead:
        // the reported tempo alternated 166, 128, 175, 134, 185 — a sawtooth, when the truth was a
        // smooth climb. Following the performance keeps the prediction locked on.
        var downbeats = new List<double>();
        var span = measureFrames;
        for (var expected = grid.FirstBeatSec * frameRate; expected < frameCount; expected += span)
        {
            var snapped = SnapToNearestOnset(detrended, frameCount, expected, searchFrames);
            downbeats.Add(snapped);

            if (downbeats.Count >= 2)
            {
                var measured = downbeats[^1] - downbeats[^2];
                // Track the player, but never let one bad snap run away with every later prediction.
                if (measured >= measureFrames / 2 && measured <= measureFrames * 2)
                {
                    span = measured;
                }
            }

            // The loop increments from `expected`, so re-anchor it on where the downbeat actually
            // landed; otherwise the snap correction is thrown away each iteration.
            expected = snapped;
        }

        if (downbeats.Count < 2)
        {
            return EmptyMap;
        }

        var raw = new double[downbeats.Count - 1];
        for (var i = 0; i < raw.Length; i++)
        {
            var spanFrames = downbeats[i + 1] - downbeats[i];
            var bpm = spanFrames > 0
                ? BeatsPerMeasure * 60.0 * frameRate / spanFrames
                : estimate.Bpm;

            // A span outside half to double the expected one means the snap found the wrong onset,
            // not that the band doubled its tempo for one bar. Fall back rather than report it.
            raw[i] = bpm < estimate.Bpm / 2 || bpm > estimate.Bpm * 2 ? estimate.Bpm : bpm;
        }

        var smoothed = MedianSmooth(raw);

        var measures = new List<TempoMeasure>(smoothed.Length);
        for (var i = 0; i < smoothed.Length; i++)
        {
            var bpm = Math.Round(smoothed[i], 1);
            measures.Add(new TempoMeasure(
                Number: i + 1,
                StartSec: downbeats[i] / frameRate,
                Bpm: bpm,
                Changed: i > 0 && Math.Abs(bpm - Math.Round(smoothed[i - 1], 1)) >= ChangeThresholdBpm));
        }

        var sorted = measures.Select(measure => measure.Bpm).Order().ToArray();
        var median = sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;

        return new TempoMap(
            MedianBpm: Math.Round(median, 1),
            MinBpm: sorted[0],
            MaxBpm: sorted[^1],
            IsSteady: sorted[^1] - sorted[0] <= SteadyRangeBpm,
            Confidence: estimate.Confidence,
            Measures: measures);
    }

    /// <summary>
    /// A three-point median filter over the per-measure tempos.
    ///
    /// <para>Where every beat carries equal weight — a steady quaver line with no accent on the
    /// downbeat — the snap has nothing to distinguish beat one from beat two, and alternates between
    /// two neighbouring onsets. That produced a 117/105/117/105 sawtooth on a track whose tempo never
    /// moved, and reported it as "not steady". A median of three removes a one-measure excursion
    /// outright while leaving a genuine ramp untouched, because the median of three consecutive
    /// rising values is the middle one.</para>
    ///
    /// <para>The first and last measures keep their raw value: there is no window around them, and
    /// borrowing one would shift the ends of the curve.</para>
    /// </summary>
    private static double[] MedianSmooth(double[] values)
    {
        if (values.Length < 3)
        {
            return values;
        }

        var smoothed = new double[values.Length];
        smoothed[0] = values[0];
        smoothed[^1] = values[^1];
        for (var i = 1; i < values.Length - 1; i++)
        {
            var a = values[i - 1];
            var b = values[i];
            var c = values[i + 1];
            smoothed[i] = Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));
        }
        return smoothed;
    }

    /// <summary>
    /// The strongest onset within <paramref name="searchFrames"/> of <paramref name="predictedFrame"/>,
    /// or the prediction itself when nothing nearby stands out.
    /// </summary>
    private static double SnapToNearestOnset(
        double[] detrended, int frameCount, double predictedFrame, int searchFrames)
    {
        var centre = (int)Math.Round(predictedFrame);
        var from = Math.Max(0, centre - searchFrames);
        var to = Math.Min(frameCount - 1, centre + searchFrames);

        var bestFrame = centre;
        var bestValue = double.NegativeInfinity;
        for (var frame = from; frame <= to; frame++)
        {
            if (detrended[frame] > bestValue)
            {
                bestValue = detrended[frame];
                bestFrame = frame;
            }
        }

        // A flat stretch of envelope has no onset to snap to; keeping the prediction there stops a
        // silent passage from inventing a tempo change.
        return bestValue > 0 ? bestFrame : predictedFrame;
    }

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
