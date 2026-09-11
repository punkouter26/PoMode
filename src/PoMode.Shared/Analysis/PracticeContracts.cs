namespace PoMode.Shared.Analysis;

/// <summary>
/// What a practice exercise drills. Each kind asks for a different piece of evidence that the singer
/// has the mode in their ear, which is why the grader reports its components separately rather than
/// collapsing everything into one number.
/// </summary>
public enum ModeExerciseKind
{
    /// <summary>Up and back down the mode's own scale. Tests the note set, nothing else.</summary>
    ScaleRun,

    /// <summary>Tonic, the degree that names the mode, tonic. The interval a mode lives or dies by.</summary>
    CharacteristicLeap,

    /// <summary>A seeded phrase over the mode's pitch pool — the note set used musically.</summary>
    Phrase,
}

/// <summary>How one target note was sung, or that it was not.</summary>
public enum NoteVerdict
{
    /// <summary>The right pitch in the right octave.</summary>
    Hit,

    /// <summary>The right pitch class in the wrong octave — a range problem, not an ear problem.</summary>
    Octave,

    /// <summary>A different note.</summary>
    Wrong,

    /// <summary>Nothing was sung near this note's place in the bar.</summary>
    Missed,
}

/// <summary>
/// One exercise: the phrase to sing, plus everything needed to say what it is asking for.
///
/// <para>Regenerated deterministically from <see cref="ModeExerciseKind"/>, mode, tonic, tempo, seed
/// and octave, exactly as a Mode Lab backing is — so an attempt can carry those six values instead of
/// echoing the whole note list back, and the server grades against the phrase it actually issued.</para>
///
/// <para><paramref name="Instruction"/> and <paramref name="Title"/> are worded server-side like every
/// other musical statement in the app: naming a degree, or saying what a mode's colour is, is a
/// judgment the client is not allowed to make.</para>
/// </summary>
public sealed record ModeExerciseDto(
    ModeExerciseKind Kind,
    string Title,
    string Instruction,
    ScaleMode Mode,
    int TonicPitchClass,
    string TonicName,
    double Bpm,
    int Seed,
    int Octave,
    IReadOnlyList<string> ScaleNotes,
    IReadOnlyList<string> CharacteristicDegrees,
    IReadOnlyList<NoteEvent> TargetNotes,
    double DurationSec);

/// <summary>
/// An attempt at an exercise: the six values that regenerate it, and what the browser heard.
///
/// <para>The notes come from the same collector the Live page uses, which rounds pitch to the nearest
/// semitone. That rounding is why nothing here is reported in cents — the data cannot support it, and
/// a cents figure derived from integers would be an invention.</para>
/// </summary>
public sealed record ExerciseAttemptRequest(
    ModeExerciseKind Kind,
    ScaleMode Mode,
    int TonicPitchClass,
    double Bpm,
    int Seed,
    int Octave,
    IReadOnlyList<NoteEvent> SungNotes);

/// <summary>One target note and what the singer did with it.</summary>
public sealed record NoteGradeDto(
    int Index,
    int TargetMidi,
    string TargetLabel,
    double TargetStartSec,
    NoteVerdict Verdict,
    int? SungMidi,
    string? SungLabel,
    double? TimingOffsetSec);

/// <summary>
/// A graded attempt. Three components rather than one score, because they fail for different reasons
/// and call for different practice: pitch is the ear, timing is the pulse, and purity is whether the
/// singer stayed inside the mode at all — a phrase can be perfectly in time and perfectly wrong.
///
/// <para><paramref name="Verdict"/> and <paramref name="Advice"/> are written here, next to the
/// numbers they describe, for the same reason the song fingerprint is: a weak figure is omitted
/// rather than hedged.</para>
/// </summary>
public sealed record ExerciseScoreDto(
    int Score,
    int PitchPercent,
    int TimingPercent,
    int ModePurityPercent,
    bool CharacteristicSung,
    int NotesHit,
    int NotesTotal,
    int ExtraNotes,
    double MedianTimingOffsetSec,
    bool Rushing,
    IReadOnlyList<string> OutsideNotes,
    IReadOnlyList<NoteGradeDto> Notes,
    string Verdict,
    string? Advice);
