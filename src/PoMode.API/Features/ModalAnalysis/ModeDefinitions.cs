using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>
/// The modal engine's scoring data: characteristic-degree weights layered over the interval sets,
/// which live in <see cref="ScaleModes"/> (Shared) so the client can spell scales from the same
/// source of truth. Masks are computed, never written as literals.
/// </summary>
public static class ModeDefinitions
{
    public static IReadOnlyList<ScaleMode> All => ScaleModes.All;

    public static IReadOnlyList<int> Intervals(ScaleMode mode) => ScaleModes.Intervals(mode);

    public static IReadOnlyList<int> CharacteristicIntervals(ScaleMode mode)
        => ScaleModes.CharacteristicIntervals(mode);

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
