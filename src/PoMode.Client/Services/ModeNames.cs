using PoMode.Shared.Analysis;

namespace PoMode.Client.Services;

/// <summary>
/// How the Mode Lab names a mode of the key on screen ("E Dorian" in the key of D) and which colour
/// class it wears. Shared by the lab and its strips, so a card, the banner and the home-note view
/// cannot spell one mode two ways.
/// </summary>
public static class ModeNames
{
    /// <summary>The mode named on its own root: the parent key's note at the mode's degree.</summary>
    public static string OnRoot(int parentTonic, ScaleMode mode)
        => $"{ScaleModes.NoteName((parentTonic + ScaleModes.ModeDegreeOffset(mode)) % 12)} {mode}";

    public static string CssClass(ScaleMode mode) => mode switch
    {
        ScaleMode.Ionian => "mode-ionian",
        ScaleMode.Dorian => "mode-dorian",
        ScaleMode.Phrygian => "mode-phrygian",
        ScaleMode.Lydian => "mode-lydian",
        ScaleMode.Mixolydian => "mode-mixolydian",
        ScaleMode.Aeolian => "mode-aeolian",
        ScaleMode.Locrian => "mode-locrian",
        _ => "",
    };

    /// <summary>The MIDI pitches of the mode from its root at or above <paramref name="baseMidi"/>,
    /// for voicing the interface's sounds in the mode being played.</summary>
    public static int[] Pitches(int parentTonic, ScaleMode mode, int baseMidi = 60)
    {
        var root = (parentTonic + ScaleModes.ModeDegreeOffset(mode)) % 12;
        var tonic = baseMidi + ((((root - baseMidi) % 12) + 12) % 12);
        return [.. ScaleModes.Intervals(mode).Select(interval => tonic + interval)];
    }
}
