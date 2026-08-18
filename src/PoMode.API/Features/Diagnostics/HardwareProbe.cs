using System.Text.Json;
using PoMode.API.Infrastructure;
using PoMode.Shared.Hardware;

namespace PoMode.API.Features.Hardware;

/// <summary>Runtime capability probe feeding /diag and (in later phases) executor availability.</summary>
public sealed class HardwareProbe(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ModelRegistry modelRegistry)
{
    public async Task<HardwareReport> ProbeAsync(CancellationToken ct)
    {
        var isAzure = EnvironmentDetector.IsAzureHosted();
        var gpu = isAzure ? null : NvmlInterop.TryProbe();
        var ollamaModels = isAzure ? [] : await ProbeOllamaAsync(ct);
        var providers = ProviderKeys.All
            .Where(key => !string.IsNullOrEmpty(configuration[key]))
            .ToArray();
        var models = modelRegistry.StatusFor(ModelCatalog.All);
        return new HardwareReport(isAzure, gpu, ollamaModels, providers, models);
    }

    private async Task<IReadOnlyList<string>> ProbeOllamaAsync(CancellationToken ct)
    {
        // Same loopback-only policy as the copilot itself: a remote BaseUrl must not make /diag
        // report models the copilot will refuse to use (nor trigger an outbound call).
        if (!OllamaEndpoint.TryResolveLoopbackBaseUrl(configuration, out var baseUrl, out _))
        {
            return [];
        }
        try
        {
            using var client = httpClientFactory.CreateClient("ollama-probe");
            client.Timeout = TimeSpan.FromSeconds(1);
            return await OllamaEndpoint.ListInstalledModelsAsync(client, baseUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return [];
        }
    }
}
