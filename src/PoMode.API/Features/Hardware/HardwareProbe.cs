using System.Text.Json;
using PoMode.API.Infrastructure;
using PoMode.Shared.Hardware;

namespace PoMode.API.Features.Hardware;

/// <summary>Runtime capability probe feeding /diag and (in later phases) executor availability.</summary>
public sealed class HardwareProbe(IConfiguration configuration, IHttpClientFactory httpClientFactory)
{
    public async Task<HardwareReport> ProbeAsync(CancellationToken ct)
    {
        var isAzure = EnvironmentDetector.IsAzureHosted();
        var gpu = isAzure ? null : NvmlInterop.TryProbe();
        var ollamaModels = isAzure ? [] : await ProbeOllamaAsync(ct);
        var providers = ProviderKeys.All
            .Where(key => !string.IsNullOrEmpty(configuration[key]))
            .ToArray();
        return new HardwareReport(isAzure, gpu, ollamaModels, providers);
    }

    private async Task<IReadOnlyList<string>> ProbeOllamaAsync(CancellationToken ct)
    {
        try
        {
            using var client = httpClientFactory.CreateClient("ollama-probe");
            client.Timeout = TimeSpan.FromSeconds(1);
            using var response = await client.GetAsync("http://localhost:11434/api/tags", ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.GetProperty("models").EnumerateArray()
                .Select(model => model.GetProperty("name").GetString())
                .Where(name => name is not null)
                .Select(name => name!)
                .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return [];
        }
    }
}
