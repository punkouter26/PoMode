namespace PoMode.Shared.Analysis;

/// <summary>
/// How consecutive melody notes move. "Step" is 1-2 semitones, "leap" is 3 or more — the
/// conventional split, and the one that tells a singer how hard a line is to pitch.
/// </summary>
public sealed record MotionProfile(
    int IntervalCount,
    int RepeatCount,
    int StepCount,
    int LeapCount,
    double RepeatPercent,
    double StepPercent,
    double LeapPercent,
    double AverageIntervalSemitones);

/// <summary>The single widest jump in the melody, with the moment it happens.</summary>
public sealed record LeapHighlight(
    int Semitones,
    bool Ascending,
    double AtSec,
    string FromLabel,
    string ToLabel);

/// <summary>
/// Overall melodic direction. <paramref name="Shape"/> is one of Rising, Falling, Arch, Valley or
/// Level — decided here rather than in the client, like every other musical judgment.
///
/// <para><paramref name="NetSemitones"/> measures the same thing differently: first note to last,
/// where <paramref name="Shape"/> compares the averages of the outer thirds. The two can disagree on
/// a line that ends high after a long descent, which is informative to a reader looking at both but
/// merely confusing in prose — so <c>InterpretationPrompt</c> withholds this one from the LLMs.</para>
/// </summary>
public sealed record ContourProfile(
    int RisingCount,
    int FallingCount,
    double RisingPercent,
    int NetSemitones,
    string Shape);

/// <summary>
/// Where the voice actually sits, as opposed to its extremes. Range says "A3 to E5"; tessitura says
/// "but it lives between D4 and B4". Low/High are the 10th and 90th percentile of sung pitches, so a
/// single showpiece high note cannot widen it.
/// </summary>
public sealed record TessituraProfile(
    int MedianMidi,
    string MedianLabel,
    int LowMidi,
    string LowLabel,
    int HighMidi,
    string HighLabel,
    int SpanSemitones);

/// <summary>One bar of the note-length histogram, e.g. "Eighth" x 214.</summary>
public sealed record NoteValueBucket(string Label, int Count, double Percent);

/// <summary>
/// Where note onsets fall against the beat grid. A note is "on the beat" when it starts nearer a beat
/// than the midpoint between beats, and "syncopated" when it starts nearer that midpoint — the classic
/// "on the and". Both are null-safe: when the beat grid is unusable the percentages are zero and
/// <paramref name="BeatGridUsable"/> is false, so the client can say so instead of showing a made-up 0%.
/// </summary>
public sealed record RhythmProfile(
    bool BeatGridUsable,
    double OnBeatPercent,
    double SyncopationPercent,
    IReadOnlyList<NoteValueBucket> NoteValues);

/// <summary>
/// Phrases, found by splitting the melody wherever the singer rests longer than
/// <c>SongStatsBuilder.PhraseGapSec</c>. This is the closest thing the data has to "breaths".
/// </summary>
public sealed record PhraseProfile(
    int Count,
    double AverageSec,
    double AverageNotes,
    double LongestSec,
    double LongestStartSec);

/// <summary>How fast the harmony moves. Beats are null-safe the same way <see cref="RhythmProfile"/> is.</summary>
public sealed record HarmonicRhythmProfile(
    bool BeatGridUsable,
    double AverageChordSec,
    double AverageChordBeats,
    double ChordsPerMinute);

/// <summary>A two-chord move and how often the song makes it.</summary>
public sealed record ChordMove(string From, string To, int Count);

/// <summary>One chord symbol and its share of the song's chord time.</summary>
public sealed record ChordCount(string Symbol, int Count, double Percent);

public sealed record ChordVocabularyProfile(
    int UniqueChords,
    IReadOnlyList<ChordCount> TopChords,
    IReadOnlyList<ChordMove> TopMoves);

/// <summary>
/// How the melody sits against the chord underneath it — a different question from "is it in the
/// scale". Percentages are of the notes that had a chord sounding with a parseable root; notes over
/// silence or an unrecognised symbol are excluded rather than counted as tension.
/// </summary>
public sealed record ChordToneProfile(
    int ClassifiedNotes,
    double RootPercent,
    double ThirdPercent,
    double FifthPercent,
    double SeventhPercent,
    double TensionPercent,
    double ConsonancePercent);

/// <summary>
/// One candidate mode's showing in the whole-song vote, for the "why this mode?" chart.
///
/// <para>The modal engine decides a mode per window, not once for the song, so the honest way to
/// show its reasoning is the tally: how many windows each mode won, and how sure it was when it won.
/// <paramref name="CharacteristicDegrees"/> names the degrees that separate this mode from its
/// neighbours — the actual evidence, e.g. the natural 6th that makes Dorian rather than Aeolian.</para>
/// </summary>
public sealed record ModeVote(
    string Mode,
    int WindowsWon,
    double WindowPercent,
    double AverageConfidence,
    bool IsPrimary,
    IReadOnlyList<string> CharacteristicDegrees);

/// <summary>
/// How often the melody sings one of the twelve degrees, for the scale-usage chart.
///
/// <para><paramref name="Interval"/> is semitones above the tonic (0-11). A degree is
/// <paramref name="InPrimaryMode"/> when the whole-song mode contains it, and
/// <paramref name="IsCharacteristic"/> when it is one of the degrees that identifies that mode —
/// so a reader can see the mode being chosen by the notes actually sung.</para>
/// </summary>
public sealed record DegreeUsage(
    int Interval,
    string DegreeLabel,
    string NoteName,
    int NoteCount,
    double Percent,
    bool InPrimaryMode,
    bool IsCharacteristic);

/// <summary>
/// Every derived statistic for one analysed song, plus <paramref name="Fingerprint"/> — the same
/// numbers written out as one plain-English paragraph. Derived on demand from the stored artifacts,
/// never persisted, so <paramref name="SchemaVersion"/> has nothing to migrate yet; it exists so a
/// future stored form can be versioned, matching <see cref="VisualizationPayload"/>.
/// </summary>
public sealed record SongStats(
    int SchemaVersion,
    string TonicName,
    string? PrimaryMode,
    double PrimaryConfidence,
    double TempoBpm,
    bool TempoEstimated,
    double DurationSec,
    int MelodyNoteCount,
    double NotesPerSecond,
    double AverageNoteSec,
    double InScalePercent,
    int ModulationCount,
    MotionProfile Motion,
    LeapHighlight? BiggestLeap,
    ContourProfile Contour,
    TessituraProfile? Tessitura,
    RhythmProfile Rhythm,
    PhraseProfile Phrases,
    HarmonicRhythmProfile HarmonicRhythm,
    ChordVocabularyProfile ChordVocabulary,
    ChordToneProfile ChordTones,
    IReadOnlyList<ModeVote> ModeVotes,
    IReadOnlyList<DegreeUsage> ScaleDegrees,
    TempoMapDto? TempoMap,
    string Fingerprint);

/// <summary>
/// One written interpretation of a song's statistics, for two audiences.
///
/// <para><paramref name="Text"/> is the plain-English summary for someone with no theory training.
/// <paramref name="TheoryText"/> is the same song discussed in proper terms for a trained musician,
/// and is null when the writer produced only one summary — the client then omits that section rather
/// than showing an empty heading.</para>
///
/// <para><paramref name="UsedLlm"/> is false when the deterministic template wrote it, so the client
/// can label the difference honestly rather than implying a model was involved.</para>
/// </summary>
public sealed record SongInterpretationDto(
    string Interpreter,
    ExecutionTier Tier,
    bool UsedLlm,
    string Text,
    string? TheoryText);

/// <summary>
/// One selectable interpreter for the picker. Unlike <see cref="ExecutorOptionDto"/> this does list
/// the Cloud entry: naming it is the only way to reach it, so hiding it would make it unreachable
/// rather than merely non-default.
/// </summary>
public sealed record InterpreterOptionDto(
    string Name,
    ExecutionTier Tier,
    bool Available,
    bool IsDefault,
    bool UsesLlm);
