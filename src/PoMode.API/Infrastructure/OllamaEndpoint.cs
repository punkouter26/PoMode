using System.Text.Json;

namespace PoMode.API.Infrastructure;

/// <summary>
/// The one place that knows where the local Ollama lives (<c>Copilot:BaseUrl</c>, defaulting to
/// loopback) and how to read the installed-model names out of its <c>/api/tags</c> answer.
/// </summary>
public static class OllamaEndpoint
{
    public const string DefaultBaseUrl = "http://localhost:11434";

    public static string ResolveBaseUrl(IConfiguration configuration)
        => (configuration["Copilot:BaseUrl"] ?? DefaultBaseUrl).TrimEnd('/');

    /// <summary>
    /// The loopback-only policy, shared by every Ollama consumer (copilot, health check, hardware
    /// probe): configurable enough for tests to stand up a fixture server, never enough that a
    /// configuration change quietly points the app at a remote host.
    /// </summary>
    public static bool TryResolveLoopbackBaseUrl(
        IConfiguration configuration, out string baseUrl, out string? rejection)
    {
        baseUrl = ResolveBaseUrl(configuration);
        rejection = null;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            rejection = "The configured copilot address is not a valid URL.";
            return false;
        }
        if (uri.IsLoopback)
        {
            return true;
        }

        rejection = "The copilot only ever talks to a local Ollama, and the configured address is not local.";
        return false;
    }

    /// <summary>Installed model names from <c>/api/tags</c>, in Ollama's own order.</summary>
    public static async Task<IReadOnlyList<string>> ListInstalledModelsAsync(
        HttpClient client, string baseUrl, CancellationToken ct)
    {
        using var response = await client.GetAsync($"{baseUrl}/api/tags", ct);
        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!document.RootElement.TryGetProperty("models", out var models))
        {
            return [];
        }

        return models.EnumerateArray()
            .Select(model => model.TryGetProperty("name", out var name) ? name.GetString() : null)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .ToArray();
    }
}
