using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>Persisted per-job state (jobs/{id}/job.json). Mutable: the pipeline updates it as stages run.</summary>
public sealed class JobState
{
    public required string JobId { get; init; }
    public required string InputFileName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public JobStage Stage { get; set; } = JobStage.Uploaded;
    public double Progress { get; set; }
    public List<StagePlan> Plan { get; set; } = [];
    public List<string> CompletedStages { get; set; } = [];
    public string? Error { get; set; }

    public JobStatusDto ToDto() => new(JobId, Stage, Progress, Plan, CompletedStages, Error, CreatedAt);
}
