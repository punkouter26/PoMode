namespace PoMode.Shared.Analysis;

public enum MelodyStyle
{
    Lyrical,
    Arpeggiated,
    Syncopated,
    Motific,
}

/// <summary>
/// Which pitch a progression's roman numerals count up from. Pop progressions are written as degrees
/// of the parent major key, so I is the key itself. Modal progressions are written from the mode's
/// own tonic, so i is the mode root. Rooting the second kind on the parent yields chords that carry
/// notes the melody's scale does not have, and asserts the wrong note as home.
/// </summary>
public enum HarmonicRoot
{
    ParentTonic,
    ModeRoot,
}

public sealed record ChordProgressionDefinition(
    string Id,
    string Name,
    string Category,
    string RomanNumerals,
    IReadOnlyList<string> ChordFormulas,
    string Description,
    ScaleMode SuggestedMode,
    double DefaultBpm,
    HarmonicRoot RootsOn = HarmonicRoot.ParentTonic,
    bool IsModeSignature = false);

/// <summary>
/// Pure lookups over the progression catalog, shared by the API and the client so neither has to
/// restate which cadence belongs to which mode.
/// </summary>
public static class ProgressionCatalog
{
    /// <summary>
    /// The cadence that puts <paramref name="mode"/>'s own tonic in the position of home, so the mode
    /// is audible as a colour rather than as the parent key with a displaced melody. Null when the
    /// catalog carries no signature for that mode.
    /// </summary>
    public static ChordProgressionDefinition? SignatureFor(
        this IEnumerable<ChordProgressionDefinition> catalog, ScaleMode mode)
        => catalog.FirstOrDefault(p => p.IsModeSignature && p.SuggestedMode == mode);

    /// <summary>
    /// A progression that stays put as the mode changes, because its numerals count from the parent
    /// key. It is what makes the other lesson possible: one unchanging harmony under all seven modes,
    /// showing they are drawn from a single set of notes.
    /// </summary>
    public static ChordProgressionDefinition? FirstSharedHarmony(
        this IEnumerable<ChordProgressionDefinition> catalog)
        => catalog.FirstOrDefault(p => p.RootsOn == HarmonicRoot.ParentTonic);
}

public sealed record ModalMelodyRequest(
    int TonicPitchClass,
    ScaleMode Mode,
    string ProgressionId,
    double Bpm = 100.0,
    MelodyStyle Style = MelodyStyle.Lyrical,
    int Seed = 42,
    int Octave = 4,
    double TargetPurity = 90.0);

public sealed record GeneratedMelodyDto(
    string ProgressionId,
    string ProgressionName,
    int TonicPitchClass,
    string TonicName,
    ScaleMode Mode,
    double Bpm,
    MelodyStyle Style,
    int Seed,
    double ModePercentage,
    string CharacteristicExplanation,
    IReadOnlyList<string> ScaleNotes,
    IReadOnlyList<NoteEvent> MelodyNotes,
    IReadOnlyList<NoteEvent> BackingNotes,
    IReadOnlyList<ChordSpan> Chords,
    VisualizationPayload Visual,
    ModalResult ModalAnalysis);

public sealed record ModeComparisonItemDto(
    ScaleMode Mode,
    string ModeName,
    string Mood,
    string CharacteristicTone,
    double ModePercentage,
    IReadOnlyList<string> ScaleNotes,
    IReadOnlyList<NoteEvent> MelodyNotes,
    VisualizationPayload Visual);

public sealed record ModalComparisonResponse(
    string ProgressionId,
    string ProgressionName,
    int TonicPitchClass,
    string TonicName,
    double Bpm,
    IReadOnlyList<ChordSpan> Chords,
    IReadOnlyList<NoteEvent> BackingNotes,
    IReadOnlyList<ModeComparisonItemDto> Modes);
