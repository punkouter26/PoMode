using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>
/// The modal engine's scoring data: characteristic-degree weights layered over the interval sets,
/// which live in <see cref="ScaleModes"/> (Shared) so the client can spell scales from the same
/// source of truth. Masks are computed, never written as literals.
/// </summary>
public static class ModeDefinitions
{
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

    public static IReadOnlyList<ScaleMode> All => ScaleModes.All;

    public static IReadOnlyList<int> Intervals(ScaleMode mode) => ScaleModes.Intervals(mode);

    public static IReadOnlyList<int> CharacteristicIntervals(ScaleMode mode) => Characteristic[mode];

    public static int Mask(ScaleMode mode)
    {
        var mask = 0;
        foreach (var interval in ScaleModes.Intervals(mode))
        {
            mask |= 1 << interval;
        }
        return mask;
    }
}
