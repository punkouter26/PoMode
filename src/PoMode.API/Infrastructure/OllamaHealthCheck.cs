using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace PoMode.API.Infrastructure;

/// <summary>
/// Reports whether the local Ollama copilot is reachable. Degraded — never Unhealthy — because
/// an absent copilot is a normal state the app fully supports (spec §5), so it must not turn
/// /health into a 503.
/// </summary>
public sealed class OllamaHealthCheck(IConfiguration configuration, IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!OllamaEndpoint.TryResolveLoopbackBaseUrl(configuration, out var baseUrl, out var rejection))
        {
            return HealthCheckResult.Degraded(rejection!);
        }
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(2);
            using var response = await client.GetAsync($"{baseUrl}/api/tags", cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("Ollama reachable.")
                : HealthCheckResult.Degraded($"Ollama answered {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Degraded("Ollama is not running — the copilot is unavailable.");
        }
    }
}
