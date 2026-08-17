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

public sealed record StagePlan(string Stage, ExecutionTier Tier, string Executor);

public sealed record JobStatusDto(
    string JobId,
    JobStage Stage,
    double Progress,
    IReadOnlyList<StagePlan> Plan,
    IReadOnlyList<string> CompletedStages,
    string? Error,
    DateTimeOffset CreatedAt);
