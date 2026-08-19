namespace PoMode.API.Features.Analysis;

/// <summary>Hourly sweep deleting job folders and batch manifests older than 7 days, plus a size
/// cap: when the store outgrows the budget, the oldest completed jobs go first.</summary>
public sealed class JobCleanupService(
    JobStore store,
    Batch.BatchStore batches,
    ILogger<JobCleanupService> logger) : BackgroundService
{
    /// <summary>~50 average jobs of stem WAVs — generous locally, bounded on small disks.</summary>
    private const long MaxStoreBytes = 2L * 1024 * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var purged = store.PurgeOlderThan(TimeSpan.FromDays(7))
                    + batches.PurgeOlderThan(TimeSpan.FromDays(7))
                    + store.PurgeToSizeBudget(MaxStoreBytes);
                if (purged > 0)
                {
                    logger.LogInformation("Purged {Count} expired job folder(s) / batch manifest(s).", purged);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job cleanup sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
