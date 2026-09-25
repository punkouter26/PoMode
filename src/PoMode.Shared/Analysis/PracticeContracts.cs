namespace PoMode.Shared.Analysis;

/// <summary>
/// The caller's earlier takes over one backing — a mirror of what their melodies turned out to be,
/// not a grade. <paramref name="Summary"/> is the one sentence the page shows, worded server-side
/// because it names modes; <paramref name="Takes"/> is newest first.
/// </summary>
public sealed record TakeHistoryDto(string Summary, IReadOnlyList<PastTakeDto> Takes);

/// <summary>
/// One earlier take. <paramref name="FoundMode"/> is the analyzer's mode name for the melody, null
/// while it is still being read or when no mode was clear; <paramref name="Reading"/> is the phrase
/// to show for it either way. <paramref name="SungOver"/> names the key and tempo of that take's
/// backing, which are allowed to differ between rows of one history.
/// </summary>
public sealed record PastTakeDto(
    string JobId,
    DateTimeOffset CreatedAt,
    JobStage Stage,
    string? FoundMode,
    string Reading,
    string SungOver);

/// <summary>
/// Where the caller's voice sits, pooled across their recent finished hum takes. The pitch fields
/// and <paramref name="Fit"/> are null until there is enough to say anything, and
/// <paramref name="TakesNeeded"/> says how far off that is. <paramref name="Summary"/> is the
/// sentence to show in both cases. <paramref name="Voice"/> is the voice type and strongest note from
/// the same takes, present once the range is.
/// </summary>
public sealed record VocalRangeDto(
    int TakeCount,
    int NoteCount,
    int TakesNeeded,
    int? LowMidi,
    string? LowLabel,
    int? HighMidi,
    string? HighLabel,
    string Summary,
    VoiceFitDto? Fit,
    VoiceProfileDto? Voice = null);

/// <summary>
/// The key that puts one mode where the caller sings. <paramref name="TonicPitchClass"/> is the
/// parent key, the same field <see cref="ModalMelodyRequest.TonicPitchClass"/> carries, so the client
/// applies it without doing any music theory of its own.
/// </summary>
public sealed record VoiceFitDto(ScaleMode Mode, int TonicPitchClass, string Explanation);
