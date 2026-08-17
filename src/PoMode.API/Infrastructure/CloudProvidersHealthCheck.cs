using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoMode.API.Features.Cloud;
using PoMode.API.Features.Hardware;

namespace PoMode.API.Infrastructure;

/// <summary>
/// Informational check: reports whether the cloud tier is enabled and which provider keys are
/// PRESENT (never their values). Always Healthy — missing keys are a normal local-dev state.
/// </summary>
public sealed class CloudProvidersHealthCheck(CloudCredentials credentials, IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object> { ["cloudEnabled"] = credentials.Enabled };
        foreach (var key in ProviderKeys.All)
        {
            data[key] = !string.IsNullOrEmpty(configuration[key]);
        }
        var configured = ProviderKeys.All.Count(key => !string.IsNullOrEmpty(configuration[key]));
        return Task.FromResult(HealthCheckResult.Healthy(
            $"Cloud tier {(credentials.Enabled ? "enabled" : "disabled")}; {configured} provider key(s) configured.",
            data));
    }
}
