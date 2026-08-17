namespace PoMode.Shared.Analysis;

/// <summary>
/// How a note relates to its harmonic context, decided server-side (spec §7's canvas colouring).
/// The client maps these to CSS variables and draws; it makes no musical decisions of its own.
/// </summary>
public enum NoteRole
{
    /// <summary>In the triad of the chord sounding when the note starts.</summary>
    ChordTone,

    /// <summary>In the active mode, but not one of its characteristic degrees.</summary>
    InMode,

    /// <summary>A degree that distinguishes the active mode from its neighbours (♮6 Dorian, ♯4 Lydian, …).</summary>
    Characteristic,

    /// <summary>Neither a chord tone nor in the active mode.</summary>
    Outside,
}

/// <summary>One note capsule in the piano-roll lane, with the dual label §7 asks for.</summary>
public sealed record VisualNote(
    int MidiPitch,
    double StartSec,
    double DurationSec,
    int Velocity,
    NoteRole Role,
    string PitchLabel,
    string DegreeLabel);

/// <summary>One chord block in the lower lane. <paramref name="ModeTag"/> is null when the window had no usable mode.</summary>
public sealed record VisualChord(
    string Symbol,
    double StartSec,
    double EndSec,
    int MeasureNumber,
    string? ModeTag);

/// <summary>
/// Everything the canvas needs, flattened into one payload so the client makes a single request and
/// the Blazor render tree never holds per-note state. SchemaVersion lets later phases migrate.
/// </summary>
public sealed record VisualizationPayload(
    int SchemaVersion,
    IReadOnlyList<VisualNote> Notes,
    IReadOnlyList<VisualChord> Chords,
    double DurationSec,
    int MinPitch,
    int MaxPitch);
