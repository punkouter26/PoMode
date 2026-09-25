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
    string HowJudged,
    VoiceMapDto? Map = null);

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

/// <summary>
/// Everything a keyboard drawing of the voice needs, decided server-side: the stretch of keyboard to
/// show, where the singing sits (10th, 50th and 90th percentile notes, the same figures the summary
/// sentence quotes), each classical type's range, and the key labels to print. Null when the voice
/// could not be judged.
/// </summary>
public sealed record VoiceMapDto(
    int FromMidi,
    int ToMidi,
    int LowMidi,
    int MedianMidi,
    int HighMidi,
    IReadOnlyList<VoiceTypeRangeDto> Types,
    IReadOnlyList<VoiceKeyLabelDto> Labels);

/// <summary>One classical voice type's range; <paramref name="Matched"/> marks the type (and any
/// leaning) the summary names.</summary>
public sealed record VoiceTypeRangeDto(VoiceType Type, string Label, int LowMidi, int HighMidi, bool Matched);

/// <summary>A key worth naming on the drawing: each C, plus the notes the sentences mention.</summary>
public sealed record VoiceKeyLabelDto(int Midi, string Label);
