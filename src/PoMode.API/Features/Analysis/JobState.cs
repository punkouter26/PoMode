using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>Persisted per-job state (jobs/{id}/job.json). Mutable: the pipeline updates it as stages run.</summary>
public sealed class JobState
{
    public required string JobId { get; init; }
    public required string InputFileName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public JobStage Stage { get; set; } = JobStage.Uploaded;

    /// <summary>
    /// Derived, never stored: the completed-stage count is the one source of truth, so the
    /// percent can never disagree with the checklist. In-stage fractions are a transient SignalR
    /// hint layered on top by the pipeline (see <see cref="ToDto"/>'s liveProgress overload).
    /// </summary>
    public double Progress => Stage == JobStage.Complete ? 1.0 : Math.Min(CompletedStages.Count, 4) / 4.0;

    public List<StagePlan> Plan { get; set; } = [];
    public List<string> CompletedStages { get; set; } = [];
    public List<StageRecord> StageHistory { get; set; } = [];
    public string? Error { get; set; }

    /// <summary>Headline facts stamped by the pipeline at completion, so listings (the library)
    /// never have to open result.json. Null on jobs persisted before this field existed.</summary>
    public string? TonicName { get; set; }
    public string? PrimaryMode { get; set; }
    public double? TempoBpm { get; set; }

    /// <summary>What this job's audio is, when the server knows — a hum take names the progression
    /// it was sung over. Null for an ordinary upload, which is its own complete explanation.</summary>
    public TakeOrigin? Origin { get; set; }

    /// <summary>The signed-in user (guest or Microsoft) who created the job, as <c>PoUser.IdOf</c>
    /// reads it. Settable because a guest who signs in with Microsoft takes their library with
    /// them. Null on jobs from before ownership existed; those belong to no one's library.</summary>
    public string? OwnerId { get; set; }

    public JobStatusDto ToDto(double? liveProgress = null)
        => new(JobId, Stage, liveProgress ?? Progress, Plan, CompletedStages, Error, CreatedAt, StageHistory, InputFileName, Origin);
}
