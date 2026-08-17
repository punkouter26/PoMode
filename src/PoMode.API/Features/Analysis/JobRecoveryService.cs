using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// Re-enqueues jobs a restart interrupted. The pipeline is already restart-safe (it skips
/// CompletedStages), but nothing put interrupted jobs back on the queue — a job killed
/// mid-stage stayed parked in a running-looking state forever.
///
/// AwaitingClient is included deliberately: unlike a browser that merely went away, the
/// re-run parks the job again and the client-delegation timeout then falls through to the
/// next tier, so the job still finishes instead of stalling.
/// </summary>
public sealed class JobRecoveryService(JobStore store, JobQueue queue, ILogger<JobRecoveryService> logger) : BackgroundService
{
    private static readonly JobStage[] Interrupted =
    [
        JobStage.Uploaded,
        JobStage.Separating,
        JobStage.PitchTracking,
        JobStage.AwaitingClient,
        JobStage.ChordDetecting,
        JobStage.ModalAnalysis,
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Directory.Exists(store.RootPath))
        {
            return;
        }

        var resumable = new List<JobState>();
        foreach (var dir in Directory.GetDirectories(store.RootPath))
        {
            var jobId = Path.GetFileName(dir);
            if (!JobId.IsValid(jobId))
            {
                continue;
            }
            try
            {
                var state = await store.LoadAsync(jobId, stoppingToken);
                if (state is not null && Interrupted.Contains(state.Stage))
                {
                    resumable.Add(state);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // One unreadable job folder must not stop the rest from being restored.
                logger.LogWarning(ex, "Could not inspect job {JobId} during recovery.", jobId);
            }
        }

        foreach (var state in resumable.OrderBy(s => s.CreatedAt))
        {
            logger.LogInformation("Re-enqueueing job {JobId} interrupted in {Stage}.", state.JobId, state.Stage);
            try
            {
                await queue.EnqueueAsync(state.JobId, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
