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
    public List<StageRecord> StageHistory { get; set; } = [];
    public string? Error { get; set; }

    /// <summary>Headline facts stamped by the pipeline at completion, so listings (the library)
    /// never have to open result.json. Null on jobs persisted before this field existed.</summary>
    public string? TonicName { get; set; }
    public string? PrimaryMode { get; set; }
    public double? TempoBpm { get; set; }

    public JobStatusDto ToDto() => new(JobId, Stage, Progress, Plan, CompletedStages, Error, CreatedAt, StageHistory, InputFileName);
}
