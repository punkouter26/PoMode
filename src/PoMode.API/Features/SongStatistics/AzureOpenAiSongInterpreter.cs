using System.Net.Http.Json;
using System.Text.Json;
using PoMode.API.Features.Cloud;
using PoMode.API.Features.Diagnostics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// The remote LLM tier: Azure OpenAI chat completions.
///
/// <para>The key arrives through the ordinary configuration chain — Key Vault first, environment
/// variable as the logged fallback — under the <see cref="ProviderKeys"/> name
/// <c>AzureOpenAiApiKey</c>, so it is resolved exactly like every other paid provider and never
/// lives in appsettings. The endpoint and deployment are not secrets and are plain configuration.</para>
///
/// <para>Being <see cref="ExecutionTier.Cloud"/> has a deliberate consequence:
/// <see cref="Pipeline.ExecutionPlanner.IsUserSelectable"/> excludes it, so it is never chosen
/// automatically and never picked by a stray query parameter. Spending the user's money on an
/// interpretation has to be an explicit, named request.</para>
/// </summary>
public sealed class AzureOpenAiSongInterpreter(
    IConfiguration configuration,
    CloudCredentials credentials,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<AzureOpenAiSongInterpreter> logger) : ISongInterpreter
{
    /// <summary>The provider key name, shared with <see cref="ProviderKeys"/> and therefore /diag.</summary>
    public const string ApiKeyName = "AzureOpenAiApiKey";

    /// <summary>A dated GA version, pinned so a service-side default change cannot alter behaviour.</summary>
    private const string DefaultApiVersion = "2024-10-21";

    public string Name => nameof(AzureOpenAiSongInterpreter);

    public ExecutionTier Tier => ExecutionTier.Cloud;

    private string? Endpoint => Trimmed(configuration["Llm:AzureOpenAi:Endpoint"])?.TrimEnd('/');

    private string? Deployment => Trimmed(configuration["Llm:AzureOpenAi:Deployment"]);

    private string ApiVersion =>
        Trimmed(configuration["Llm:AzureOpenAi:ApiVersion"]) ?? DefaultApiVersion;

    /// <summary>
    /// Configured means all three of endpoint, deployment and key. Reported without calling Azure:
    /// a probe request would cost money and tell us nothing the caller cannot discover by asking.
    /// </summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => Task.FromResult(Endpoint is not null && Deployment is not null && credentials.Has(ApiKeyName));

    public async Task<string> InterpretAsync(SongStats stats, CancellationToken ct)
    {
        if (Endpoint is not { } endpoint || Deployment is not { } deployment)
        {
            throw new InvalidOperationException(
                "Azure OpenAI is not configured: set Llm:AzureOpenAi:Endpoint and Llm:AzureOpenAi:Deployment.");
        }

        if (credentials.TokenFor(ApiKeyName) is not { } key)
        {
            throw new InvalidOperationException(
                $"Azure OpenAI is not configured: no '{ApiKeyName}' secret resolved.");
        }

        if (!credentials.Enabled)
        {
            throw new InvalidOperationException("The cloud tier is switched off (Cloud:Enabled=false).");
        }

        var url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}";
        var body = new
        {
            messages = new[]
            {
                new { role = "system", content = InterpretationPrompt.System },
                new { role = "user", content = InterpretationPrompt.User(stats) },
            },
            temperature = 0.4,
            max_tokens = 700,
        };

        using var client = httpClientFactory.CreateClient(nameof(AzureOpenAiSongInterpreter));
        using var response = await ResilientHttp.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body),
                Headers = { { "api-key", key } },
            },
            time, logger, ct);

        if (!response.IsSuccessStatusCode)
        {
            // The status only; an Azure error body can echo request content, and this one carries
            // the whole prompt.
            throw new InvalidOperationException(
                $"Azure OpenAI refused the request ({(int)response.StatusCode}) for deployment '{deployment}'.");
        }

        var text = ReadContent(await response.Content.ReadAsStringAsync(ct));
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"Azure OpenAI returned an empty interpretation for deployment '{deployment}'.");
        }

        logger.LogInformation("Interpreted song statistics with Azure OpenAI deployment {Deployment}.", deployment);
        return text.Trim();
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
                ? content.GetString()
                : null;
    }
}
