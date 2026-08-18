namespace PoMode.Shared.Analysis;

/// <summary>
/// The one home for binary searches over time-ordered lists (see the Shared carve-out in
/// CLAUDE.md). Both searches assume the list is sorted by start time; the covering search
/// additionally assumes non-overlapping half-open <c>[start, end)</c> spans, so a boundary
/// time belongs to the later item.
/// </summary>
public static class TimelineSearch
{
    /// <summary>Index of the item whose span covers <paramref name="timeSec"/>, or null when
    /// the time falls outside every span (before the first, after the last, or in a gap).</summary>
    public static int? IndexCovering<T>(
        IReadOnlyList<T> items, double timeSec, Func<T, double> startOf, Func<T, double> endOf)
    {
        var low = 0;
        var high = items.Count - 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var item = items[mid];
            if (timeSec < startOf(item))
            {
                high = mid - 1;
            }
            else if (timeSec >= endOf(item))
            {
                low = mid + 1;
            }
            else
            {
                return mid;
            }
        }
        return null;
    }

    /// <summary>First index whose start time is at or after <paramref name="timeSec"/> (the
    /// lower bound); <c>items.Count</c> when every item starts earlier.</summary>
    public static int LowerBound<T>(IReadOnlyList<T> items, double timeSec, Func<T, double> startOf)
    {
        var low = 0;
        var high = items.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (startOf(items[mid]) < timeSec)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }
        return low;
    }
}
