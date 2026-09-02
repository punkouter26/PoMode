namespace PoMode.Shared.Analysis;

public enum MelodyStyle
{
    Lyrical,
    Arpeggiated,
    Syncopated,
    Motific,
}

public sealed record ChordProgressionDefinition(
    string Id,
    string Name,
    string Category,
    string RomanNumerals,
    IReadOnlyList<string> ChordFormulas,
    string Description,
    ScaleMode SuggestedMode,
    double DefaultBpm);

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
