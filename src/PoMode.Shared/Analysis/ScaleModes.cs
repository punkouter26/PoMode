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

    /// <summary>Fractional MIDI number of a frequency (A4 = 440 Hz = 69) — the one converter,
    /// shared by the pitch tracker and the chroma extractor.</summary>
    public static double MidiFromFrequency(double frequencyHz)
        => 69 + (12 * Math.Log2(frequencyHz / 440.0));
}
