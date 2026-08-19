namespace PoMode.API.Features.ModalAnalysis;

public static class PitchNames
{
    private static readonly string[] Degrees = ["1", "b2", "2", "b3", "3", "4", "#4", "5", "b6", "6", "b7", "7"];

    /// <summary>Delegates to the Shared table so API and Client always spell pitches identically.</summary>
    public static string Name(int pitchClass) => PoMode.Shared.Analysis.ScaleModes.NoteName(pitchClass);

    public static string IntervalLabel(int semitones) => Degrees[((semitones % 12) + 12) % 12];

    /// <summary>Semitones a pitch sits above the tonic, 0-11 — the app's "scale degree" formula.</summary>
    public static int IntervalAboveTonic(int midiPitch, int tonicPitchClass)
        => ((((midiPitch % 12) + 12) % 12) - tonicPitchClass + 12) % 12;

    /// <summary>
    /// The one chord-root parser: a natural letter followed by any run of accidentals
    /// (<c>#</c>/<c>♯</c>/<c>b</c>/<c>♭</c>). Returns false rather than throwing for anything
    /// unrecognised ("N", an older artifact schema), so callers degrade — to "no chord tones",
    /// a skipped MIDI chord, or a raw symbol — instead of failing a request.
    /// </summary>
    public static bool TryParseRoot(string? root, out int pitchClass)
    {
        pitchClass = 0;
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var baseClass = char.ToUpperInvariant(root[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11,
            _ => -1,
        };
        if (baseClass < 0)
        {
            return false;
        }

        foreach (var accidental in root.AsSpan(1))
        {
            switch (accidental)
            {
                case '#' or '♯': baseClass++; break;
                case 'b' or '♭': baseClass--; break;
                default: return false;
            }
        }

        pitchClass = ((baseClass % 12) + 12) % 12;
        return true;
    }
}
