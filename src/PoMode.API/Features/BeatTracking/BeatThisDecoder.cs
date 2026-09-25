using PoMode.API.Audio;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.BeatTracking;

/// <summary>
/// Beat This!'s logits → beat and downbeat times → the pipeline's beat grid and tempo map. Pure
/// functions, no model.
///
/// <para>The peak picking is the reference "minimal" postprocessor (<c>Postprocessor.postp_minimal</c>
/// in CPJKU/beat_this), which is what the model was designed and evaluated around — no DBN: a frame is
/// a beat when its logit is the maximum within ±3 frames (±60 ms at 50 fps) and above 0 (p &gt; 0.5);
/// peaks at most one frame apart are merged to their mean; each downbeat then snaps to its nearest
/// beat.</para>
/// </summary>
public static class BeatThisDecoder
{
    public const double FramesPerSecond = 50.0;

    private const int PeakRadius = 3;
    private const int DeduplicateWidth = 1;

    /// <summary>A beat interval this far from the median is an irregular beat for the confidence figure.</summary>
    private const double RegularityTolerance = 0.15;

    /// <summary>Fewer beats than this is not a grid anyone should click along to.</summary>
    private const int MinimumBeats = 4;

    /// <summary>The shortest bar kept, in beats: anything shorter is a spurious downbeat.</summary>
    private const double MinimumBarBeats = 1.5;

    /// <summary>Same bound as the classic tempo map's step check (<see cref="TempoEstimator"/>).</summary>
    private const double MaxStepFraction = 0.25;

    public static (double[] Beats, double[] Downbeats) Decode(float[] beatLogits, float[] downbeatLogits)
    {
        var beats = PickPeaks(beatLogits);
        var downbeats = PickPeaks(downbeatLogits);
        if (beats.Length > 0)
        {
            for (var i = 0; i < downbeats.Length; i++)
            {
                var nearest = beats[0];
                foreach (var beat in beats)
                {
                    if (Math.Abs(beat - downbeats[i]) < Math.Abs(nearest - downbeats[i]))
                    {
                        nearest = beat;
                    }
                }
                downbeats[i] = nearest;
            }
            downbeats = [.. downbeats.Distinct().Order()];
        }
        return (beats, downbeats);
    }

    private static double[] PickPeaks(float[] logits)
    {
        var peaks = new List<int>();
        for (var frame = 0; frame < logits.Length; frame++)
        {
            if (logits[frame] <= 0)
            {
                continue;
            }
            var isMax = true;
            for (var j = Math.Max(0, frame - PeakRadius); j <= Math.Min(logits.Length - 1, frame + PeakRadius); j++)
            {
                if (logits[j] > logits[frame])
                {
                    isMax = false;
                    break;
                }
            }
            if (isMax)
            {
                peaks.Add(frame);
            }
        }

        // deduplicate_peaks: a run of peaks each within DeduplicateWidth frames of the running mean
        // collapses to that mean (a flat-topped maximum reports every frame of its plateau).
        var times = new List<double>(peaks.Count);
        var index = 0;
        while (index < peaks.Count)
        {
            double mean = peaks[index];
            var count = 1;
            index++;
            while (index < peaks.Count && peaks[index] - mean <= DeduplicateWidth)
            {
                count++;
                mean += (peaks[index] - mean) / count;
                index++;
            }
            times.Add(mean / FramesPerSecond);
        }
        return [.. times];
    }

    /// <summary>
    /// The beats as the two artifacts the rest of the app reads. The grid's tempo comes from the
    /// regular beat intervals, anchored on the first downbeat so the metronome's accent lands on beat
    /// one; its confidence is how regular the beats are, not a model probability.
    ///
    /// <para>Each tempo-map measure runs from one downbeat to the next and reads its tempo from the
    /// regular beat intervals inside it, so a missed beat (one double-length interval) does not read as
    /// a 25% slowdown and a genuine 2/4 bar does not read as double time. Measures are then held to the
    /// same rule as the classic map: a bar more than 25% away from the last accepted one is a
    /// mis-detection, not rubato, and repeats the last good tempo. A downbeat closer than one and a
    /// half beats to the previous one is dropped — no bar is that short, and keeping it would put a
    /// one-beat "bar" into the measure numbering. A three-bar median then smooths the curve, as in
    /// the classic map.</para>
    /// </summary>
    public static BeatTrackResult Summarize(IReadOnlyList<double> beats, IReadOnlyList<double> downbeats, string tracker)
    {
        if (beats.Count < MinimumBeats)
        {
            return new BeatTrackResult(
                new BeatGridDto(120.0, 0.0, 0.0, Downbeats: null, Tracker: tracker),
                new TempoMapDto(0, 0, 0, IsSteady: true, 0, []),
                beats);
        }

        var intervals = new double[beats.Count - 1];
        for (var i = 1; i < beats.Count; i++)
        {
            intervals[i - 1] = beats[i] - beats[i - 1];
        }
        var medianInterval = Median(intervals);
        var regular = intervals
            .Where(interval => Math.Abs(interval - medianInterval) <= RegularityTolerance * medianInterval)
            .ToArray();
        var confidence = Math.Round(regular.Length / (double)intervals.Length, 3);
        // The mean of the regular intervals, not the median: beats sit on a 20 ms frame grid, so any
        // single interval is quantised (104 BPM reads as 103.4 or 105.0) while their mean is not.
        var bpm = Math.Round(60.0 / (regular.Length > 0 ? regular.Average() : medianInterval), 2);

        var bars = new List<double>(downbeats.Count);
        foreach (var downbeat in downbeats)
        {
            if (bars.Count == 0 || downbeat - bars[^1] >= MinimumBarBeats * medianInterval)
            {
                bars.Add(downbeat);
            }
        }

        var grid = new BeatGridDto(
            bpm,
            bars.Count > 0 ? bars[0] : beats[0],
            confidence,
            bars.Count >= 2 ? bars : null,
            tracker);

        if (bars.Count < 2)
        {
            return new BeatTrackResult(grid, new TempoMapDto(0, 0, 0, IsSteady: true, 0, []), beats);
        }

        var raw = new double[bars.Count - 1];
        var accepted = bpm;
        for (var i = 0; i < raw.Length; i++)
        {
            // The beats of this bar plus the next downbeat, so the bar's last interval counts too.
            var inside = beats.Where(beat => beat >= bars[i] - 1e-6 && beat < bars[i + 1] - 1e-6)
                .Append(bars[i + 1])
                .ToArray();
            var gaps = new double[Math.Max(0, inside.Length - 1)];
            for (var g = 0; g < gaps.Length; g++)
            {
                gaps[g] = inside[g + 1] - inside[g];
            }
            var measured = accepted;
            if (gaps.Length > 0)
            {
                // Mean of the bar's regular gaps: the median alone would snap to the 20 ms frame grid
                // (140 BPM reads as 136.4 or 142.9), the plain mean would count a missed beat.
                var typical = Median(gaps);
                // A two-gap bar can have neither gap near their median; then the median is all there is.
                var steady = gaps.Where(gap => Math.Abs(gap - typical) <= RegularityTolerance * typical).ToArray();
                measured = 60.0 / (steady.Length > 0 ? steady.Average() : typical);
            }
            if (measured >= accepted * (1 - MaxStepFraction) && measured <= accepted * (1 + MaxStepFraction))
            {
                accepted = measured;
            }
            raw[i] = accepted;
        }

        // Same three-bar median as the classic map: removes a one-bar wobble, keeps a real ramp.
        var smoothed = TempoEstimator.MedianSmooth(raw);
        var measures = new List<TempoMeasureDto>(smoothed.Length);
        for (var i = 0; i < smoothed.Length; i++)
        {
            var measureBpm = Math.Round(smoothed[i], 1);
            measures.Add(new TempoMeasureDto(
                Number: i + 1,
                StartSec: bars[i],
                Bpm: measureBpm,
                Changed: i > 0 && Math.Abs(measureBpm - measures[i - 1].Bpm) >= TempoEstimator.ChangeThresholdBpm));
        }

        var tempos = measures.Select(measure => measure.Bpm).ToArray();
        var min = tempos.Min();
        var max = tempos.Max();
        return new BeatTrackResult(grid, new TempoMapDto(
            Math.Round(Median(tempos), 1), min, max, max - min <= TempoEstimator.SteadyRangeBpm, confidence, measures),
            beats);
    }

    private static double Median(IReadOnlyCollection<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;
    }
}
