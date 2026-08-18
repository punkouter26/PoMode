using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.MusicXml;

/// <summary>
/// Chord → Roman numeral relative to the detected tonic. Case follows the chord's own quality
/// (uppercase major, lowercase minor), the convention lead sheets use. Concept ported from the
/// PoModeAg variant, adapted to this app's pitch-class-based contracts.
/// </summary>
public static class RomanNumerals
{
    private static readonly string[] Major = ["I", "bII", "II", "bIII", "III", "IV", "#IV", "V", "bVI", "VI", "bVII", "VII"];
    private static readonly string[] Minor = ["i", "bii", "ii", "biii", "iii", "iv", "#iv", "v", "bvi", "vi", "bvii", "vii"];

    public static string Analyze(ChordSpan chord, int tonicPitchClass)
    {
        if (!PitchNames.TryParseRoot(chord.Root, out var rootPitchClass))
        {
            return chord.Symbol; // "N" (no chord) or an unexpected root — show the raw symbol
        }
        var interval = (rootPitchClass - tonicPitchClass + 12) % 12;
        var minorQuality = chord.Quality is "min" or "m" or "min7" or "m7" or "dim";
        var numeral = minorQuality ? Minor[interval] : Major[interval];
        var suffix = chord.Quality.ToLowerInvariant() switch
        {
            "maj7" => "maj7",
            "7" or "min7" or "m7" => "7",
            "dim" => "°",
            "sus2" => "sus2",
            "sus4" => "sus4",
            _ => "",
        };
        return numeral + suffix;
    }
}
