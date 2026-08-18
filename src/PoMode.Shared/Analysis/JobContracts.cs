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
    Cloud,
}

/// <summary>The wire vocabulary for stage names — used in <see cref="StagePlan.Stage"/> and
/// <see cref="JobStatusDto.CompletedStages"/>, so the pipeline and the client must share it.</summary>
public static class StageNames
{
    public const string Separating = "Separating";
    public const string PitchTracking = "PitchTracking";
    public const string ChordDetecting = "ChordDetecting";
    public const string ModalAnalysis = "ModalAnalysis";

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
    string? FileName = null);

/// <summary>Request body for POST /api/analysis/from-url (yt-dlp ingest).</summary>
public sealed record AnalyzeUrlRequest(string Url);

/// <summary>Request body for POST /api/live/analyze: notes the browser transcribed from the mic.</summary>
public sealed record LiveAnalyzeRequest(IReadOnlyList<NoteEvent> Notes);

/// <summary>Reply to a live analysis: the modal result plus the ready-to-draw canvas payload,
/// both computed server-side exactly like a stored job's.</summary>
public sealed record LiveAnalysisDto(ModalResult Result, VisualizationPayload Visual);

/// <summary>One row in the song library: a persisted job plus its headline analysis once complete.</summary>
public sealed record LibraryEntryDto(
    string JobId,
    string FileName,
    DateTimeOffset CreatedAt,
    JobStage Stage,
    string? TonicName,
    string? PrimaryMode,
    double? TempoBpm);

public sealed record BatchTrackStatus(
    string JobId,
    string FileName,
    JobStage Stage,
    double Progress,
    string? Error);

public sealed record BatchStatusDto(string BatchId, IReadOnlyList<BatchTrackStatus> Tracks);
