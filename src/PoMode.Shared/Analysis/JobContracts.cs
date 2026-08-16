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
