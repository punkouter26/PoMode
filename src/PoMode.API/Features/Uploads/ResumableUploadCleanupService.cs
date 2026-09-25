namespace PoMode.API.Features.Uploads;

/// <summary>Hourly sweep of abandoned partial uploads. A phone that lost signal and never came back
/// leaves up to 100 MB behind; the sliding expiry decides when it counts as abandoned.</summary>
public sealed class ResumableUploadCleanupService(
    ResumableUploads uploads,
    ILogger<ResumableUploadCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var purged = await uploads.PurgeExpiredAsync(stoppingToken);
                if (purged > 0)
                {
                    logger.LogInformation("Purged {Count} expired partial upload(s).", purged);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Upload cleanup sweep failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
