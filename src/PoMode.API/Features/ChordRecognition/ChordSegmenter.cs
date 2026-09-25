using PoMode.API.Audio;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>
/// Turns a per-frame chord label sequence into clean <see cref="ChordSpan"/>s: median-smooths
/// away single-frame flicker, merges equal runs into spans, absorbs spans shorter than the
/// minimum duration into their longer neighbour, and drops "no chord" spans entirely.
/// </summary>
public static class ChordSegmenter
{
    /// <summary>Below this a beat grid is noise — sustained pads, silence — and chords are cut by
    /// duration instead. The pipeline applies the same bar to the beat tracker's grid.</summary>
    public const double MinBeatConfidence = 0.2;

    /// <summary>Fewer heard beats than this is not a beat track to cut chords on.</summary>
    public const int MinBeats = 4;

    /// <summary>
    /// Beat-synchronous segmentation (§13.6 fix b): chord changes land on beats, so each beat
    /// interval takes the majority label of its frames and boundaries fall exactly on the grid —
    /// no arbitrary duration floor absorbing flicker. When the grid's confidence is below
    /// <paramref name="minBeatConfidence"/> there are no beats worth trusting (sustained pads,
    /// silence), and this falls back to the duration-floor overload unchanged.
    ///
    /// <para><paramref name="beats"/>, when given, are the beats the pipeline's beat tracker heard,
    /// and they win over <paramref name="grid"/>: a model's beats follow a tempo that drifts, where a
    /// single-tempo grid slides off the music a bar at a time — and the DSP estimate behind the grid
    /// halved a 143 BPM groove that Beat This! tracked at 0.99.</para>
    /// </summary>
    public static IReadOnlyList<ChordSpan> Segment(
        IReadOnlyList<(ChordCandidate Chord, double Score)> frames,
        double framesPerSecond,
        BeatGrid? grid,
        double minDurationSec = 0.5,
        int medianWindow = 9,
        double minBeatConfidence = MinBeatConfidence,
        IReadOnlyList<double>? beats = null)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var duration = frames.Count / framesPerSecond;
        List<double> boundaries;
        if (beats is { Count: >= MinBeats })
        {
            // Beat boundaries as heard, closed at 0 and the track end; a beat within a quarter of
            // the typical gap of either end would leave a sliver interval, so it is dropped.
            var gaps = beats.Zip(beats.Skip(1), (a, b) => b - a).Order().ToArray();
            var epsilon = gaps[gaps.Length / 2] * 0.25;
            boundaries = [0.0, .. beats.Where(t => t > epsilon && t < duration - epsilon), duration];
        }
        else if (grid is not null && grid.Confidence >= minBeatConfidence && grid.Bpm > 0)
        {
            // Beat boundaries covering [0, duration]: the grid phase, then every period; 0 and the
            // track end close the partial intervals at the edges.
            var period = 60.0 / grid.Bpm;
            var epsilon = period * 0.25;
            boundaries = [0.0];
            for (var t = grid.FirstBeatSec % period; t < duration - epsilon; t += period)
            {
                if (t > epsilon)
                {
                    boundaries.Add(t);
                }
            }
            boundaries.Add(duration);
        }
        else
        {
            return Segment(frames, framesPerSecond, minDurationSec, medianWindow);
        }

        var smoothed = MedianSmooth(frames, medianWindow);

        // Majority label per beat interval; an interval too short to contain a frame centre
        // inherits its left neighbour so it merges away instead of inventing a label.
        var labels = new ChordCandidate?[boundaries.Count - 1];
        var frameIndex = 0;
        for (var interval = 0; interval < labels.Length; interval++)
        {
            var end = boundaries[interval + 1];
            var firstFrame = frameIndex;
            while (frameIndex < smoothed.Count && (frameIndex + 0.5) / framesPerSecond < end)
            {
                frameIndex++;
            }
            labels[interval] = frameIndex > firstFrame
                ? MajorityLabel(smoothed, firstFrame, frameIndex - 1)
                : interval > 0 ? labels[interval - 1] : null;
        }
        // A frameless leading interval takes the first real label so it merges forward.
        for (var interval = labels.Length - 2; interval >= 0; interval--)
        {
            labels[interval] ??= labels[interval + 1];
        }

        var spans = new List<(ChordCandidate Chord, double Start, double End)>();
        for (var interval = 0; interval < labels.Length; interval++)
        {
            if (labels[interval] is not { } label)
            {
                continue;
            }
            if (spans.Count > 0 && spans[^1].Chord.Equals(label))
            {
                spans[^1] = (label, spans[^1].Start, boundaries[interval + 1]);
            }
            else
            {
                spans.Add((label, boundaries[interval], boundaries[interval + 1]));
            }
        }

        return ToChordSpans(spans);
    }

    public static IReadOnlyList<ChordSpan> Segment(
        IReadOnlyList<(ChordCandidate Chord, double Score)> frames,
        double framesPerSecond,
        double minDurationSec = 0.5,
        int medianWindow = 9)
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var smoothed = MedianSmooth(frames, medianWindow);
        var spans = MergeRuns(smoothed, framesPerSecond);
        AbsorbShortSpans(spans, minDurationSec);
        var merged = MergeAdjacentEqual(spans);

        return ToChordSpans(merged);
    }

    /// <summary>Emits the final spans, dropping "N" (no chord) entries.</summary>
    private static ChordSpan[] ToChordSpans(List<(ChordCandidate Chord, double Start, double End)> spans)
        => spans
            .Where(s => s.Chord.Symbol != "N")
            .Select(s => new ChordSpan(s.Chord.Symbol, s.Chord.Root, s.Chord.Quality, s.Start, s.End))
            .ToArray();

    private static List<ChordCandidate> MedianSmooth(
        IReadOnlyList<(ChordCandidate Chord, double Score)> frames, int medianWindow)
    {
        var n = frames.Count;
        var radius = Math.Max(0, medianWindow) / 2;

        var labels = new List<ChordCandidate>(n);
        for (var i = 0; i < n; i++)
        {
            labels.Add(frames[i].Chord);
        }

        var result = new List<ChordCandidate>(n);
        for (var i = 0; i < n; i++)
        {
            var start = Math.Max(0, i - radius);
            var end = Math.Min(n - 1, i + radius);
            result.Add(MajorityLabel(labels, start, end, i));
        }
        return result;
    }

    /// <summary>
    /// Mode of the labels in [start, end] (inclusive); ties broken by <paramref name="centreIndex"/>'s
    /// label when given and among the tied, otherwise by first-seen order.
    /// </summary>
    private static ChordCandidate MajorityLabel(
        IReadOnlyList<ChordCandidate> labels, int start, int end, int? centreIndex = null)
    {
        var counts = new Dictionary<ChordCandidate, int>();
        var firstSeenOrder = new List<ChordCandidate>();
        for (var i = start; i <= end; i++)
        {
            var candidate = labels[i];
            if (!counts.ContainsKey(candidate))
            {
                counts[candidate] = 0;
                firstSeenOrder.Add(candidate);
            }
            counts[candidate]++;
        }

        var maxCount = counts.Values.Max();
        var tied = firstSeenOrder.Where(c => counts[c] == maxCount).ToList();
        if (tied.Count == 1)
        {
            return tied[0];
        }

        if (centreIndex is { } centre)
        {
            var centreLabel = labels[centre];
            if (tied.Contains(centreLabel))
            {
                return centreLabel;
            }
        }
        return tied[0];
    }

    private static List<(ChordCandidate Chord, double Start, double End)> MergeRuns(
        List<ChordCandidate> smoothed, double framesPerSecond)
    {
        var spans = new List<(ChordCandidate Chord, double Start, double End)>();
        var runStart = 0;
        for (var i = 1; i <= smoothed.Count; i++)
        {
            if (i == smoothed.Count || !smoothed[i].Equals(smoothed[runStart]))
            {
                spans.Add((smoothed[runStart], runStart / framesPerSecond, i / framesPerSecond));
                runStart = i;
            }
        }
        return spans;
    }

    /// <summary>
    /// Repeatedly absorbs the first remaining sub-minimum-duration span into whichever neighbour
    /// (by current duration; ties favour the left/earlier neighbour) is longer, extending that
    /// neighbour's boundary to cover the absorbed span exactly. A span with no neighbour (i.e. the
    /// only span in the list) is left as-is.
    /// </summary>
    private static void AbsorbShortSpans(
        List<(ChordCandidate Chord, double Start, double End)> spans, double minDurationSec)
    {
        while (spans.Count > 1)
        {
            var shortIndex = spans.FindIndex(s => s.End - s.Start < minDurationSec);
            if (shortIndex < 0)
            {
                break;
            }

            var hasLeft = shortIndex > 0;
            var hasRight = shortIndex < spans.Count - 1;
            var target = spans[shortIndex];

            var absorbLeft = hasLeft &&
                (!hasRight || spans[shortIndex - 1].End - spans[shortIndex - 1].Start
                    >= spans[shortIndex + 1].End - spans[shortIndex + 1].Start);

            if (absorbLeft)
            {
                var left = spans[shortIndex - 1];
                spans[shortIndex - 1] = (left.Chord, left.Start, target.End);
            }
            else
            {
                var right = spans[shortIndex + 1];
                spans[shortIndex + 1] = (right.Chord, target.Start, right.End);
            }

            spans.RemoveAt(shortIndex);
        }
    }

    /// <summary>Combines contiguous, equal-symbol spans that absorption may have reunited.</summary>
    private static List<(ChordCandidate Chord, double Start, double End)> MergeAdjacentEqual(
        List<(ChordCandidate Chord, double Start, double End)> spans)
    {
        var merged = new List<(ChordCandidate Chord, double Start, double End)>();
        foreach (var span in spans)
        {
            if (merged.Count > 0 && merged[^1].Chord.Equals(span.Chord) && merged[^1].End == span.Start)
            {
                var last = merged[^1];
                merged[^1] = (last.Chord, last.Start, span.End);
            }
            else
            {
                merged.Add(span);
            }
        }
        return merged;
    }
}
