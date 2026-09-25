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

    /// <summary>Owner prefix for jobs the server keeps for itself (the demo template). No sign-in path
    /// can mint it, so such a job is in nobody's library, and the purge sweeps leave it alone.</summary>
    public const string ServerOwnerPrefix = "system:";

    public bool IsServerOwned => OwnerId?.StartsWith(ServerOwnerPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Marks a stage done without running it — its artifact was handed over by whoever queued the job —
    /// and rewrites its plan entry to name what actually happened. The plan is what the client reports
    /// as "who ran" (and what the mock-data banner reads), so leaving the planned executor in place
    /// would be a false claim about the job.
    /// </summary>
    public void MarkProvided(string stage, string executor, DateTimeOffset now)
    {
        if (CompletedStages.Contains(stage))
        {
            return;
        }

        StageHistory.Add(new StageRecord(stage, ExecutionTier.Local, executor, now, now));
        CompletedStages.Add(stage);

        var index = Plan.FindIndex(p => p.Stage == stage);
        if (index >= 0)
        {
            Plan[index] = Plan[index] with
            {
                Tier = ExecutionTier.Local,
                Executor = executor,
                IsPlaceholder = false,
            };
        }
    }

    /// <summary>
    /// This job's state under a new id, owner and creation time, for a copy whose artifacts are copied
    /// beside it — the copy is finished work, never re-run. Every stored property is carried here, so
    /// a new one added above has to be added here too or copies silently lose it.
    /// </summary>
    public JobState CopyAs(string jobId, string ownerId, DateTimeOffset createdAt) => new()
    {
        JobId = jobId,
        InputFileName = InputFileName,
        CreatedAt = createdAt,
        Stage = Stage,
        Plan = [.. Plan],
        CompletedStages = [.. CompletedStages],
        StageHistory = [.. StageHistory],
        Error = Error,
        TonicName = TonicName,
        PrimaryMode = PrimaryMode,
        TempoBpm = TempoBpm,
        Origin = Origin,
        OwnerId = ownerId,
    };

    public JobStatusDto ToDto(double? liveProgress = null)
        => new(JobId, Stage, liveProgress ?? Progress, Plan, CompletedStages, Error, CreatedAt, StageHistory, InputFileName, Origin);
}
