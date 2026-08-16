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
