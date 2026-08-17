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
        var rootPitchClass = ParsePitchClass(chord.Root);
        if (rootPitchClass < 0)
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

    /// <summary>"C" → 0 … "B" → 11; supports # and b accidentals. -1 when unparseable.</summary>
    public static int ParsePitchClass(string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return -1;
        }
        var natural = char.ToUpperInvariant(root[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
            _ => -1,
        };
        if (natural < 0)
        {
            return -1;
        }
        var shift = root[1..] switch
        {
            "" => 0,
            "#" or "♯" => 1,
            "b" or "♭" => -1,
            _ => int.MinValue,
        };
        return shift == int.MinValue ? -1 : ((natural + shift) % 12 + 12) % 12;
    }
}
