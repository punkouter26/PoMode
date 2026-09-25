namespace PoMode.Shared.Analysis;

/// <summary>The six classical voice types, lowest first. Named by range only: the same label is
/// used whoever is singing, because a pitch track says where a voice sits, not whose it is.</summary>
public enum VoiceType
{
    Bass,
    Baritone,
    Tenor,
    Alto,
    MezzoSoprano,
    Soprano,
}

/// <summary>
/// What a pitch track can say about a singer: which voice type's range the singing sits in, and the
/// note they sing best. <paramref name="Type"/> and everything after it are null when there was too
/// little singing to judge; <paramref name="Summary"/> is the sentence to show either way.
/// <paramref name="Leaning"/> is the neighbouring type when the voice sits between two.
/// </summary>
public sealed record VoiceProfileDto(
    string Summary,
    VoiceType? Type,
    string? TypeLabel,
    string? Leaning,
    string? TessituraLabel,
    int? StrongestMidi,
    string? StrongestLabel,
    string? StrongestSentence,
    IReadOnlyList<VoiceNoteDto> Notes,
    string HowJudged);

/// <summary>
/// One sung pitch, pooled over every time it was sung. <paramref name="OffCents"/> is the average
/// distance from true pitch and <paramref name="WobbleCents"/> how much the pitch moved while held;
/// both are null when the audio could not be re-measured, in which case only time and count speak.
/// </summary>
public sealed record VoiceNoteDto(
    int Midi,
    string Label,
    int Count,
    double HeldSec,
    double? OffCents,
    double? WobbleCents);
