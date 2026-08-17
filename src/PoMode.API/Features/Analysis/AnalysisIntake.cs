using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// The one path from "validated audio stream" to "queued job": persist, plan, enqueue.
/// Shared by the upload endpoint, the URL ingest endpoint, and the batch endpoint so the
/// planning-before-response contract (the response DTO already reflects the plan) holds everywhere.
/// </summary>
public sealed class AnalysisIntake(JobStore store, JobQueue queue, ExecutionPlanner planner)
{
    public async Task<JobState> StartAsync(string fileName, Stream content, bool clientCanInfer, CancellationToken ct)
    {
        var state = await store.CreateAsync(fileName, content, ct);
        try
        {
            state.Plan = await planner.PlanAsync(clientCanInfer, ct);
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
        await store.SaveAsync(state, ct);
        await queue.EnqueueAsync(state.JobId, ct);
        return state;
    }
}
