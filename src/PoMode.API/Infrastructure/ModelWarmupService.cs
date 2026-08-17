namespace PoMode.API.Infrastructure;

/// <summary>
/// Downloads <see cref="ModelCatalog"/> models sequentially in the background after the host has
/// started, so local inference tiers (e.g. <c>OnnxPitchTracker</c>) become reachable without a
/// user-triggered download — without this, <c>IsAvailableAsync</c> only ever sees "not downloaded" and
/// the planner never selects the local tier. No-ops in Azure mode (spec §5: local models are
/// desktop/on-prem only) and when explicitly disabled via <c>Models:AutoDownload</c>. Because
/// <see cref="BackgroundService.ExecuteAsync"/> runs after startup completes, this never blocks
/// application startup — the first upload may still use a fake tracker while a model downloads, later
/// ones use the real model.
/// </summary>
/// <param name="catalog">
/// Models to warm up; defaults to <see cref="ModelCatalog.All"/>. Overridable only so tests can point at
/// a local fixture instead of downloading real (multi-hundred-MB) models.
/// </param>
public sealed class ModelWarmupService(
    ModelRegistry registry,
    IConfiguration configuration,
    ILogger<ModelWarmupService> logger,
    IReadOnlyList<ModelDescriptor>? catalog = null) : BackgroundService
{
    private readonly IReadOnlyList<ModelDescriptor> _catalog = catalog ?? ModelCatalog.All;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (EnvironmentDetector.IsAzureHosted())
        {
            return;
        }

        if (configuration.GetValue("Models:AutoDownload", true) is false)
        {
            return;
        }

        foreach (var descriptor in _catalog)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (registry.IsDownloaded(descriptor))
            {
                continue;
            }

            try
            {
                logger.LogInformation("Warming up model {Key} from {Url}...", descriptor.Key, descriptor.Url);
                var path = await registry.EnsureAsync(descriptor, stoppingToken);
                logger.LogInformation(
                    "Warmed up model {Key} ({SizeBytes} bytes).", descriptor.Key, new FileInfo(path).Length);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to warm up model {Key}; it remains unavailable until a later attempt.",
                    descriptor.Key);
            }
        }
    }
}
