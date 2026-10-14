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
                await PublishQueueAsync(stoppingToken);
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

    /// <summary>Everyone still waiting has just moved up one. Best-effort: a push that fails must not
    /// cost the job that was just taken its run.</summary>
    private async Task PublishQueueAsync(CancellationToken stoppingToken)
    {
        var waiting = queue.Waiting();
        for (var i = 0; i < waiting.Length; i++)
        {
            try
            {
                // Same count as JobQueue.Ahead: the job now running, plus those in front.
                await notifier.PublishQueueAsync(waiting[i], i + 1, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Queue position push failed for job {JobId}.", waiting[i]);
            }
        }
    }
}
