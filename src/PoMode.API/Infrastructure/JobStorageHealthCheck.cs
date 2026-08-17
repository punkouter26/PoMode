using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoMode.API.Features.Analysis;

namespace PoMode.API.Infrastructure;

/// <summary>Verifies the job artifact root is writable and reports the blob mirror's reachability.</summary>
public sealed class JobStorageHealthCheck(IConfiguration configuration, JobBlobStorage? blobs = null) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var root = !string.IsNullOrEmpty(configuration["Jobs:RootPath"])
                ? configuration["Jobs:RootPath"]!
                : Path.Combine(AppContext.BaseDirectory, "jobs");
            Directory.CreateDirectory(root);
            var probe = Path.Combine(root, $".healthprobe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Job storage not writable.", ex);
        }

        return blobs is null
            ? HealthCheckResult.Healthy("Job storage writable.")
            : await blobs.ProbeAsync(cancellationToken) switch
            {
                BlobMirrorStatus.Connected => HealthCheckResult.Healthy("Job storage writable; blob mirror connected."),
                BlobMirrorStatus.Disabled => HealthCheckResult.Healthy("Job storage writable; blob mirror disabled by configuration."),
                _ => HealthCheckResult.Degraded(
                    "Job storage writable, but the blob mirror is unreachable — start Azurite with 'docker compose up -d' and restart the API."),
            };
    }
}
