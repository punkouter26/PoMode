using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// The one path from "validated audio stream" to "queued job": persist, plan, enqueue.
/// Shared by the upload endpoint and the Mode Lab endpoints so the planning-before-response
/// contract (the response DTO already reflects the plan) holds everywhere.
/// </summary>
public sealed class AnalysisIntake(JobStore store, JobQueue queue, ExecutionPlanner planner)
{
    /// <param name="seed">
    /// Optional: writes artifacts the caller already knows, and marks the stages that would have
    /// produced them complete, before the job is queued. Runs inside the same window as planning
    /// for a reason — the worker must not be able to dequeue the job and start detecting what the
    /// caller was about to hand it. A Mode Lab hum take is the case that needs it: the chords the
    /// user sang over are a setting they chose, not something to rediscover from a solo hum.
    /// </param>
    public async Task<JobState> StartAsync(
        string fileName,
        Stream content,
        bool clientCanInfer,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? preferredExecutors = null,
        Func<JobState, CancellationToken, Task>? seed = null,
        string? ownerId = null,
        string? jobId = null)
    {
        var state = await store.CreateAsync(fileName, content, ct, ownerId, jobId);
        try
        {
            state.Plan = await planner.PlanAsync(clientCanInfer, preferredExecutors, ct);
        }
        catch (InvalidOperationException ex)
        {
            // No executor is available for some stage: report a Failed job (the pipeline's own
            // graceful-failure shape) instead of letting this surface as a 500.
            state.Stage = JobStage.Failed;
            state.Error = ex.Message;
            await store.SaveAsync(state, ct);
            return state;
        }
        if (seed is not null)
        {
            await seed(state, ct);
        }
        await store.SaveAsync(state, ct);
        await queue.EnqueueAsync(state.JobId, ct);
        return state;
    }

    /// <summary>
    /// A drum take for the Mode Lab: stored as an ordinary library job, so it is restart-safe,
    /// blob-mirrored and listed, but never planned or queued. Separating, transcribing and
    /// chord-reading a beat held the single worker for minutes to produce a "song" nobody read.
    /// </summary>
    public async Task<JobState> KeepBeatAsync(string fileName, Stream content, string? ownerId, CancellationToken ct)
    {
        var state = await store.CreateAsync(fileName, content, ct, ownerId);
        state.Origin = new TakeOrigin(TakeOrigin.Beat, "Beat · kept for the Mode Lab's drum loop, not analysed");
        state.Stage = JobStage.Complete;
        await store.SaveAsync(state, ct);
        return state;
    }
}
