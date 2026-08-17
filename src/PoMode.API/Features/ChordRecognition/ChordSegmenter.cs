using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordRecognition;

/// <summary>
/// Turns a per-frame chord label sequence into clean <see cref="ChordSpan"/>s: median-smooths
/// away single-frame flicker, merges equal runs into spans, absorbs spans shorter than the
/// minimum duration into their longer neighbour, and drops "no chord" spans entirely.
/// </summary>
public static class ChordSegmenter
{
    /// <summary>
    /// Segments a per-frame chord label sequence into time-ordered <see cref="ChordSpan"/>s.
    /// Pipeline: median-filter labels over <paramref name="medianWindow"/> frames (mode of the
    /// window, ties broken by the centre frame) → merge equal runs into spans → absorb spans
    /// shorter than <paramref name="minDurationSec"/> into the longer neighbour → drop "N"
    /// (no chord) spans from the output.
    /// </summary>
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

        return merged
            .Where(s => s.Chord.Symbol != "N")
            .Select(s => new ChordSpan(s.Chord.Symbol, s.Chord.Root, s.Chord.Quality, s.Start, s.End))
            .ToArray();
    }

    private static List<ChordCandidate> MedianSmooth(
        IReadOnlyList<(ChordCandidate Chord, double Score)> frames, int medianWindow)
    {
        var n = frames.Count;
        var radius = Math.Max(0, medianWindow) / 2;

        var result = new List<ChordCandidate>(n);
        for (var i = 0; i < n; i++)
        {
            var start = Math.Max(0, i - radius);
            var end = Math.Min(n - 1, i + radius);
            result.Add(ModeOfWindow(frames, start, end, i));
        }
        return result;
    }

    /// <summary>Mode of the chord labels in [start, end] (inclusive); ties broken by the centre frame.</summary>
    private static ChordCandidate ModeOfWindow(
        IReadOnlyList<(ChordCandidate Chord, double Score)> frames, int start, int end, int centreIndex)
    {
        var counts = new Dictionary<ChordCandidate, int>();
        var firstSeenOrder = new List<ChordCandidate>();
        for (var i = start; i <= end; i++)
        {
            var candidate = frames[i].Chord;
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

        var centreLabel = frames[centreIndex].Chord;
        return tied.Contains(centreLabel) ? centreLabel : tied[0];
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
