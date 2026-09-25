namespace PoMode.Shared.Analysis;

public enum JobStage
{
    Uploaded,
    Separating,
    PitchTracking,
    ChordDetecting,
    ModalAnalysis,
    Complete,
    Failed,
    Cancelled,

    /// <summary>
    /// Pitch tracking has been handed to the user's browser and the server is waiting for it to post
    /// results (spec §4, Tier 2). Appended rather than inserted next to <see cref="PitchTracking"/>
    /// on purpose: these values are persisted as numbers in job.json, so renumbering the existing
    /// members would silently reinterpret every job folder already on disk.
    /// </summary>
    AwaitingClient,
}

public enum ExecutionTier
{
    Local,
    ClientDelegated,
}

/// <summary>The wire vocabulary for stage names — used in <see cref="StagePlan.Stage"/> and
/// <see cref="JobStatusDto.CompletedStages"/>, so the pipeline and the client must share it.</summary>
public static class StageNames
{
    public const string Separating = "Separating";
    public const string PitchTracking = "PitchTracking";
    public const string ChordDetecting = "ChordDetecting";
    public const string ModalAnalysis = "ModalAnalysis";

    /// <summary>
    /// Upload query keys for the per-stage executor picks — the one table both the client's
    /// upload URL builder and the upload endpoint read, so the two sides can never drift.
    /// </summary>
    public static readonly IReadOnlyList<(string Stage, string QueryKey)> ExecutorQueryKeys =
    [
        (Separating, "stemSeparator"),
        (PitchTracking, "pitchTracker"),
        (ChordDetecting, "chordRecognizer"),
    ];

    /// <summary>The stage name a running job's <see cref="JobStage"/> corresponds to, or null for
    /// non-stage states. <see cref="JobStage.AwaitingClient"/> is the pitch stage parked on the
    /// browser, so it maps to <see cref="PitchTracking"/>.</summary>
    public static string? ForStage(JobStage stage) => stage switch
    {
        JobStage.Separating => Separating,
        JobStage.PitchTracking or JobStage.AwaitingClient => PitchTracking,
        JobStage.ChordDetecting => ChordDetecting,
        JobStage.ModalAnalysis => ModalAnalysis,
        _ => null,
    };
}

public static class JobStageExtensions
{
    /// <summary>True once the job can never progress again: Complete, Failed, or Cancelled.</summary>
    public static bool IsTerminal(this JobStage stage)
        => stage is JobStage.Complete or JobStage.Failed or JobStage.Cancelled;
}

/// <summary>
/// Where a job's audio came from, when the server knows more than "somebody uploaded a file". A hum
/// take is the case that needs it: the mode the analyzer reports is a fact about a melody the user
/// sang over a progression this app chose for them, and without saying so the page shows a mode on
/// an unexplained file. <paramref name="Kind"/> is a stable token for the client to branch on;
/// <paramref name="Description"/> is the sentence to show, worded server-side like every other
/// musical statement in the app.
///
/// <para><paramref name="Backing"/> is the request the loop was generated from, kept so a later take
/// over the same chords can be found again (the Practice page's take history). Null on takes from
/// before it was recorded, and on anything that is not a hum take.</para>
/// </summary>
public sealed record TakeOrigin(string Kind, string Description, ModalMelodyRequest? Backing = null)
{
    /// <summary>Sung or hummed over a Mode Lab progression.</summary>
    public const string HumTake = "HumTake";

    /// <summary>The first-run example: a piece the server synthesized in a known mode and analyzed
    /// once, copied into a new user's library so they see a finished result before uploading.</summary>
    public const string Demo = "Demo";
}

/// <summary><paramref name="IsPlaceholder"/> mirrors the executor's own placeholder flag so the
/// client can show the mock-data banner without knowing executor naming conventions.</summary>
public sealed record StagePlan(string Stage, ExecutionTier Tier, string Executor, bool IsPlaceholder = false);

/// <summary>One pipeline stage run: which executor/tier actually handled it and when. A stage
/// re-run after a restart appends a second record, so the history is an audit trail, not a set.</summary>
public sealed record StageRecord(
    string Stage,
    ExecutionTier Tier,
    string Executor,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record JobStatusDto(
    string JobId,
    JobStage Stage,
    double Progress,
    IReadOnlyList<StagePlan> Plan,
    IReadOnlyList<string> CompletedStages,
    string? Error,
    DateTimeOffset CreatedAt,
    IReadOnlyList<StageRecord>? StageHistory = null,
    string? FileName = null,
    TakeOrigin? Origin = null);

/// <summary>One row in the song library: a persisted job plus its headline analysis once complete.</summary>
public sealed record LibraryEntryDto(
    string JobId,
    string FileName,
    DateTimeOffset CreatedAt,
    JobStage Stage,
    string? TonicName,
    string? PrimaryMode,
    double? TempoBpm,
    TakeOrigin? Origin = null);
