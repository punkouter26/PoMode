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

/// <summary>One of the twelve scale-degree badges in the HUD grid (spec §7).</summary>
/// <param name="Interval">Semitones above the tonic, 0-11.</param>
/// <param name="Sung">The singer actually used this degree in this window.</param>
/// <param name="InMode">The degree belongs to the window's top-ranked mode.</param>
/// <param name="Characteristic">The degree is one that distinguishes that mode from its neighbours.</param>
public sealed record DegreeBadge(int Interval, string Label, bool Sung, bool InMode, bool Characteristic);

/// <summary>A runner-up mode for the HUD's ranked alternatives list.</summary>
public sealed record ModeAlternative(string Mode, double Confidence);

/// <summary>
/// Everything the HUD shows for one analysis window, pre-derived. <paramref name="ModeTag"/> and
/// <paramref name="ModeConfidence"/> are null when the engine found insufficient evidence — the HUD
/// must say so rather than borrow the whole-song primary mode, which would overstate the result.
/// </summary>
public sealed record VisualWindow(
    int Index,
    double StartSec,
    double EndSec,
    string ChordSymbol,
    int MeasureNumber,
    string MaskHex,
    string? ModeTag,
    double? ModeConfidence,
    bool InsufficientEvidence,
    IReadOnlyList<DegreeBadge> Degrees,
    IReadOnlyList<ModeAlternative> Alternatives);

/// <summary>
/// One step of the canvas's tempo line: this measure held this tempo from <paramref name="StartSec"/>
/// until the next point. Drawn as a step rather than interpolated, because the measurement really is
/// one value per measure and a smooth curve would imply a precision the data does not have.
/// </summary>
public sealed record VisualTempoPoint(double StartSec, double Bpm, bool Changed);

/// <summary>
/// Everything the canvas and HUD need, flattened into one payload so the client makes a single request,
/// the Blazor render tree never holds per-note state, and clicking a chord needs no round trip.
/// Derived on demand from the stored artifacts, so SchemaVersion has nothing to migrate yet — it exists
/// so a future stored form can be versioned.
/// </summary>
public sealed record VisualizationPayload(
    int SchemaVersion,
    IReadOnlyList<VisualNote> Notes,
    IReadOnlyList<VisualChord> Chords,
    IReadOnlyList<VisualWindow> Windows,
    double DurationSec,
    int MinPitch,
    int MaxPitch,
    IReadOnlyList<VisualTempoPoint> Tempo);
