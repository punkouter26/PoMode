using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Mode interval sets and their derived 12-bit masks. Masks are computed, never written as literals.</summary>
public static class ModeDefinitions
{
    private static readonly Dictionary<ScaleMode, int[]> IntervalSets = new()
    {
        [ScaleMode.Ionian] = [0, 2, 4, 5, 7, 9, 11],
        [ScaleMode.Dorian] = [0, 2, 3, 5, 7, 9, 10],
        [ScaleMode.Phrygian] = [0, 1, 3, 5, 7, 8, 10],
        [ScaleMode.Lydian] = [0, 2, 4, 6, 7, 9, 11],
        [ScaleMode.Mixolydian] = [0, 2, 4, 5, 7, 9, 10],
        [ScaleMode.Aeolian] = [0, 2, 3, 5, 7, 8, 10],
        [ScaleMode.Locrian] = [0, 1, 3, 5, 6, 8, 10],
        [ScaleMode.MinorPentatonic] = [0, 3, 5, 7, 10],
        [ScaleMode.MajorPentatonic] = [0, 2, 4, 7, 9],
    };

    /// <summary>Degrees that distinguish a mode from its nearest neighbours; weighted extra when sung.</summary>
    private static readonly Dictionary<ScaleMode, int[]> Characteristic = new()
    {
        [ScaleMode.Ionian] = [11],           // major 7
        [ScaleMode.Dorian] = [9, 3],         // natural 6 over a minor 3
        [ScaleMode.Phrygian] = [1],          // flat 2
        [ScaleMode.Lydian] = [6],            // sharp 4
        [ScaleMode.Mixolydian] = [10, 4],    // flat 7 over a major 3
        [ScaleMode.Aeolian] = [8, 3],        // flat 6 over a minor 3
        [ScaleMode.Locrian] = [6, 1],        // flat 5 and flat 2
        [ScaleMode.MinorPentatonic] = [3, 10],
        [ScaleMode.MajorPentatonic] = [4, 9],
    };

    public static IReadOnlyList<ScaleMode> All { get; } = [.. IntervalSets.Keys];

    public static IReadOnlyList<int> Intervals(ScaleMode mode) => IntervalSets[mode];

    public static IReadOnlyList<int> CharacteristicIntervals(ScaleMode mode) => Characteristic[mode];

    public static int Mask(ScaleMode mode)
    {
        var mask = 0;
        foreach (var interval in IntervalSets[mode])
        {
            mask |= 1 << interval;
        }
        return mask;
    }
}
