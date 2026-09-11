using PoMode.API.Pipeline;
using PoMode.API.Platform;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// The one path from "validated audio stream" to "queued job": persist, plan, enqueue.
/// Shared by the upload endpoint, the URL ingest endpoint, and the batch endpoint so the
/// planning-before-response contract (the response DTO already reflects the plan) holds everywhere.
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
        Func<JobState, CancellationToken, Task>? seed = null)
    {
        var state = await store.CreateAsync(fileName, content, ct);
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
        // Counted at the one place every intake path converges on, so the upload endpoint, URL
        // ingest, batch, Mode Lab and hum takes can never drift into counting differently.
        PoTelemetry.JobsStarted.Add(1,
            new KeyValuePair<string, object?>("seeded", seed is not null));
        return state;
    }
}
