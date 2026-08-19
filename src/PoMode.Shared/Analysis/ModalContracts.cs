namespace PoMode.Shared.Analysis;

public enum ScaleMode
{
    Ionian,
    Dorian,
    Phrygian,
    Lydian,
    Mixolydian,
    Aeolian,
    Locrian,
    MinorPentatonic,
    MajorPentatonic,
}

public sealed record ModalMatch(
    ScaleMode Mode,
    double Confidence,
    IReadOnlyList<int> MatchedIntervals,
    IReadOnlyList<int> OutsideIntervals);

public sealed record ModalWindow(
    int Index,
    double StartSec,
    double EndSec,
    string ChordSymbol,
    int MeasureNumber,
    int VocalMask,
    IReadOnlyList<int> SungIntervals,
    bool InsufficientEvidence,
    IReadOnlyList<ModalMatch> Matches);

/// <summary>Whole-song modal analysis. SchemaVersion lets later phases migrate old job folders.</summary>
public sealed record ModalResult(
    int SchemaVersion,
    int TonicPitchClass,
    string TonicName,
    double TonicConfidence,
    ScaleMode? PrimaryMode,
    double PrimaryConfidence,
    double TempoBpm,
    bool TempoEstimated,
    IReadOnlyList<ModalWindow> Windows);

public static class ModalResultExtensions
{
    /// <summary>Index of the window covering <paramref name="timeSec"/>, or null outside every
    /// window. Windows are time-ordered, non-overlapping and half-open <c>[Start, End)</c>.</summary>
    public static int? WindowIndexAt(this ModalResult result, double timeSec)
        => TimelineSearch.IndexCovering(
            result.Windows, timeSec, static w => w.StartSec, static w => w.EndSec);

    /// <summary>The primary mode's scale spelled as note names, tonic first — or null when no
    /// mode was named. Pure lookup over <see cref="ScaleModes"/>; both views render it.</summary>
    public static string[]? PrimaryScaleNoteNames(this ModalResult result)
        => result.PrimaryMode is { } mode ? ScaleModes.NoteNames(result.TonicPitchClass, mode) : null;
}
