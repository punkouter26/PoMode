using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoMode.API.Infrastructure;

/// <summary>Verifies the job artifact root exists and is writable.</summary>
public sealed class JobStorageHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
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
            return Task.FromResult(HealthCheckResult.Healthy("Job storage writable."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Job storage not writable.", ex));
        }
    }
}
