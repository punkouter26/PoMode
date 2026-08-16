using System.Text.Json;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>Runs the 4-stage pipeline for one job: restart-safe, tier-fallback on failure, cancellable.</summary>
public sealed class AnalysisPipeline(
    JobStore store,
    ExecutionPlanner planner,
    IEnumerable<IStemSeparator> stemSeparators,
    IEnumerable<IPitchTracker> pitchTrackers,
    IEnumerable<IChordRecognizer> chordRecognizers,
    IModalAnalyzer modalAnalyzer,
    IAnalysisNotifier notifier,
    ILogger<AnalysisPipeline> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task RunAsync(string jobId, CancellationToken ct)
    {
        var state = await store.LoadAsync(jobId, ct)
            ?? throw new InvalidOperationException($"Job {jobId} not found.");
        if (state.Stage is JobStage.Complete or JobStage.Cancelled)
        {
            // A job cancelled while still queued (DELETE before dequeue) or re-enqueued after
            // completion must not run again.
            return;
        }
        try
        {
            if (state.Plan.Count == 0)
            {
                state.Plan = await planner.PlanAsync(ct);
                await PersistAsync(state, ct);
            }

            var context = new StageContext(jobId, store.JobDir(jobId), store.InputPath(state));

            if (!state.CompletedStages.Contains(StageNames.Separating))
            {
                await EnterStageAsync(state, JobStage.Separating, 0, ct);
                await RunWithFallbackAsync(state, StageNames.Separating, stemSeparators,
                    async (executor, token) => { await executor.SeparateAsync(context, token); return true; }, ct);
                await CompleteStageAsync(state, StageNames.Separating, 0, ct);
            }

            if (!state.CompletedStages.Contains(StageNames.PitchTracking))
            {
                await EnterStageAsync(state, JobStage.PitchTracking, 1, ct);
                var notes = await RunWithFallbackAsync(state, StageNames.PitchTracking, pitchTrackers,
                    (executor, token) => executor.TrackAsync(context, token), ct);
                await WriteArtifactAsync(context.JobDir, "notes.json", notes, ct);
                await CompleteStageAsync(state, StageNames.PitchTracking, 1, ct);
            }

            if (!state.CompletedStages.Contains(StageNames.ChordDetecting))
            {
                await EnterStageAsync(state, JobStage.ChordDetecting, 2, ct);
                var chords = await RunWithFallbackAsync(state, StageNames.ChordDetecting, chordRecognizers,
                    (executor, token) => executor.RecognizeAsync(context, token), ct);
                await WriteArtifactAsync(context.JobDir, "chords.json", chords, ct);
                await CompleteStageAsync(state, StageNames.ChordDetecting, 2, ct);
            }

            if (!state.CompletedStages.Contains(StageNames.ModalAnalysis))
            {
                await EnterStageAsync(state, JobStage.ModalAnalysis, 3, ct);
                await modalAnalyzer.AnalyzeAsync(context, ct);
                await CompleteStageAsync(state, StageNames.ModalAnalysis, 3, ct);
            }

            state.Stage = JobStage.Complete;
            state.Progress = 1.0;
            await PersistAsync(state, ct);
        }
        catch (OperationCanceledException)
        {
            state.Stage = JobStage.Cancelled;
            await PersistAsync(state, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed in stage {Stage}.", jobId, state.Stage);
            state.Stage = JobStage.Failed;
            state.Error = ex.Message;
            await PersistAsync(state, CancellationToken.None);
        }
    }

    private async Task<TResult> RunWithFallbackAsync<TExecutor, TResult>(
        JobState state,
        string stage,
        IEnumerable<TExecutor> candidates,
        Func<TExecutor, CancellationToken, Task<TResult>> run,
        CancellationToken ct)
        where TExecutor : IStageExecutor
    {
        var planned = state.Plan.Single(p => p.Stage == stage);
        var ordered = candidates
            .OrderBy(c => c.Name == planned.Executor ? -1 : ExecutionPlanner.TierRank(c.Tier))
            .ToList();

        Exception? lastFailure = null;
        foreach (var candidate in ordered)
        {
            ct.ThrowIfCancellationRequested();
            if (lastFailure is not null && !await candidate.IsAvailableAsync(ct))
            {
                continue;
            }
            try
            {
                var result = await run(candidate, ct);
                if (candidate.Name != planned.Executor)
                {
                    state.Plan[state.Plan.IndexOf(planned)] = planned with { Tier = candidate.Tier, Executor = candidate.Name };
                    await PersistAsync(state, ct);
                    logger.LogWarning("Stage {Stage} fell back from {Planned} to {Actual}.", stage, planned.Executor, candidate.Name);
                }
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Executor {Executor} failed for stage {Stage}; trying next tier.", candidate.Name, stage);
                lastFailure = ex;
            }
        }
        throw lastFailure ?? new InvalidOperationException($"No executor ran for stage {stage}.");
    }

    private async Task EnterStageAsync(JobState state, JobStage stage, int index, CancellationToken ct)
    {
        state.Stage = stage;
        state.Progress = index / 4.0;
        await PersistAsync(state, ct);
    }

    private async Task CompleteStageAsync(JobState state, string stageName, int index, CancellationToken ct)
    {
        state.CompletedStages.Add(stageName);
        state.Progress = (index + 1) / 4.0;
        await PersistAsync(state, ct);
    }

    private async Task PersistAsync(JobState state, CancellationToken ct)
    {
        await store.SaveAsync(state, ct);
        await notifier.PublishAsync(state.ToDto(), ct);
    }

    private static Task WriteArtifactAsync<T>(string jobDir, string fileName, T payload, CancellationToken ct)
        => File.WriteAllTextAsync(Path.Combine(jobDir, fileName), JsonSerializer.Serialize(payload, JsonOptions), ct);
}
