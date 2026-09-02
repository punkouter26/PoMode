namespace PoMode.Shared.Analysis;

/// <summary>
/// Pure scale-mode lookups: fixed music-theory data (interval sets, sharp-spelled pitch names)
/// that both the API and the Client render from. This is the deliberate Shared carve-out — no
/// I/O, no state, no musical judgment; the API's <c>ModeDefinitions</c> layers its scoring
/// weights on top of these interval sets.
/// </summary>
public static class ScaleModes
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

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

    public static IReadOnlyList<ScaleMode> All { get; } = [.. IntervalSets.Keys];

    /// <summary>Semitone offsets above the tonic, ascending, tonic (0) first.</summary>
    public static IReadOnlyList<int> Intervals(ScaleMode mode) => IntervalSets[mode];

    /// <summary>Sharp-spelled pitch-class name — the app's one naming convention.</summary>
    public static string NoteName(int pitchClass) => Names[((pitchClass % 12) + 12) % 12];

    /// <summary>The mode's note names built on <paramref name="tonicPitchClass"/>, tonic first.</summary>
    public static string[] NoteNames(int tonicPitchClass, ScaleMode mode)
        => [.. IntervalSets[mode].Select(interval => NoteName(tonicPitchClass + interval))];

    /// <summary>Degree semitone offset of the relative mode in the parent major scale.</summary>
    public static int ModeDegreeOffset(ScaleMode mode) => mode switch
    {
        ScaleMode.Ionian => 0,          // 1st degree (e.g. C in C Major)
        ScaleMode.Dorian => 2,          // 2nd degree (e.g. D in C Major)
        ScaleMode.Phrygian => 4,        // 3rd degree (e.g. E in C Major)
        ScaleMode.Lydian => 5,          // 4th degree (e.g. F in C Major)
        ScaleMode.Mixolydian => 7,      // 5th degree (e.g. G in C Major)
        ScaleMode.Aeolian => 9,         // 6th degree (e.g. A in C Major)
        ScaleMode.Locrian => 11,        // 7th degree (e.g. B in C Major)
        ScaleMode.MajorPentatonic => 0, // Major Pentatonic (1-2-3-5-6)
        ScaleMode.MinorPentatonic => 9, // Relative Minor Pentatonic on Degree 6
        _ => 0,
    };

    /// <summary>
    /// Returns the notes of the parent key, ordered starting from the mode's root note.
    /// In C Major:
    /// C Ionian: C D E F G A B
    /// D Dorian: D E F G A B C
    /// E Phrygian: E F G A B C D
    /// F Lydian: F G A B C D E
    /// G Mixolydian: G A B C D E F
    /// A Aeolian: A B C D E F G
    /// B Locrian: B C D E F G A
    /// All notes belong 100% strictly to the parent major scale.
    /// </summary>
    public static string[] RelativeScaleNoteNames(int parentTonicPitchClass, ScaleMode mode)
    {
        var offset = ModeDegreeOffset(mode);
        var modeRootClass = (parentTonicPitchClass + offset) % 12;

        if (mode == ScaleMode.MajorPentatonic)
        {
            int[] pentaOffsets = [0, 2, 4, 7, 9];
            return [.. pentaOffsets.Select(iv => NoteName(parentTonicPitchClass + iv))];
        }
        if (mode == ScaleMode.MinorPentatonic)
        {
            int[] pentaOffsets = [0, 3, 5, 7, 10];
            return [.. pentaOffsets.Select(iv => NoteName(modeRootClass + iv))];
        }

        int[] parentIntervals = [0, 2, 4, 5, 7, 9, 11];
        var idx = Array.IndexOf(parentIntervals, offset);
        if (idx < 0) idx = 0;

        var result = new string[7];
        for (var i = 0; i < 7; i++)
        {
            var iv = parentIntervals[(idx + i) % 7];
            result[i] = NoteName(parentTonicPitchClass + iv);
        }
        return result;
    }

    /// <summary>Fractional MIDI number of a frequency (A4 = 440 Hz = 69) — the one converter,
    /// shared by the pitch tracker and the chroma extractor.</summary>
    public static double MidiFromFrequency(double frequencyHz)
        => 69 + (12 * Math.Log2(frequencyHz / 440.0));
}
