namespace PoMode.API.Features.Analysis;

/// <summary>Single-concurrency job consumer — GPU stages must not share VRAM.</summary>
public sealed class AnalysisWorker(
    JobQueue queue,
    AnalysisPipeline pipeline,
    JobCancellationRegistry cancellations,
    IAnalysisNotifier notifier,
    ILogger<AnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in queue.DequeueAllAsync(stoppingToken))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cancellations.Register(jobId, cts);
            try
            {
                // Everyone still waiting has just moved up one.
                foreach (var waiting in queue.Waiting())
                {
                    await notifier.PublishQueueAsync(waiting, queue.Ahead(waiting) ?? 0, stoppingToken);
                }
                await pipeline.RunAsync(jobId, cts.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled pipeline failure for job {JobId}.", jobId);
            }
            finally
            {
                cancellations.Remove(jobId);
            }
        }
    }
}
