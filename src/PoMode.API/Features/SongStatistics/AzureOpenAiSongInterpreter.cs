using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using PoMode.API.Features.Cloud;
using PoMode.API.Features.Diagnostics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// The remote LLM tier: an Azure AI Services chat deployment.
///
/// <para><b>Entra first, key second.</b> NET_RULES asks for managed identity via
/// <see cref="DefaultAzureCredential"/> rather than stored secrets, and this subscription has no Key
/// Vault to hold a key in, so a token is the primary path: locally it comes from the developer's
/// <c>az login</c>, and in Azure from the app's managed identity, with no secret anywhere. An
/// <c>AzureOpenAiApiKey</c> in configuration still wins when one is present, which keeps a
/// deployment in another tenant workable without a code change.</para>
///
/// <para>Being <see cref="ExecutionTier.Cloud"/> has a deliberate consequence:
/// <see cref="Pipeline.ExecutionPlanner.IsUserSelectable"/> excludes it, so it is never chosen
/// automatically and never picked up by a stray query parameter. Spending money has to be an
/// explicit, named request.</para>
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

    /// <summary>The audience for an Azure AI Services data-plane token.</summary>
    private const string TokenScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>Pinned, so a service-side default change cannot alter behaviour under us.</summary>
    private const string DefaultApiVersion = "2024-12-01-preview";

    /// <summary>
    /// Reused across calls because it caches tokens; constructing one per request would re-do the
    /// credential chain every time and make the availability probe slow.
    /// </summary>
    private static readonly DefaultAzureCredential Credential = new();

    /// <summary>Acquiring a token must not stall the interpreter picker if the chain hangs.</summary>
    private static readonly TimeSpan TokenTimeout = TimeSpan.FromSeconds(10);

    private AccessToken _token;

    public string Name => nameof(AzureOpenAiSongInterpreter);

    public ExecutionTier Tier => ExecutionTier.Cloud;

    private string? Endpoint => Trimmed(configuration["Llm:AzureOpenAi:Endpoint"])?.TrimEnd('/');

    private string? Deployment => Trimmed(configuration["Llm:AzureOpenAi:Deployment"]);

    private string ApiVersion => Trimmed(configuration["Llm:AzureOpenAi:ApiVersion"]) ?? DefaultApiVersion;

    /// <summary>
    /// Configured means an endpoint, a deployment, and some way to authenticate. The token path is
    /// probed for real rather than assumed: <see cref="DefaultAzureCredential"/> succeeds or fails
    /// silently depending on whether anyone is logged in, and reporting the tier as available when
    /// no credential exists would only produce a failure later, after the user pressed the button.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (Endpoint is null || Deployment is null || !credentials.Enabled)
        {
            return false;
        }

        return credentials.Has(ApiKeyName) || await TryGetTokenAsync(ct) is not null;
    }

    public async Task<string> InterpretAsync(SongStats stats, CancellationToken ct)
    {
        if (Endpoint is not { } endpoint || Deployment is not { } deployment)
        {
            throw new InvalidOperationException(
                "Azure is not configured: set Llm:AzureOpenAi:Endpoint and Llm:AzureOpenAi:Deployment.");
        }

        if (!credentials.Enabled)
        {
            throw new InvalidOperationException("The cloud tier is switched off (Cloud:Enabled=false).");
        }

        var key = credentials.TokenFor(ApiKeyName);
        var bearer = key is null ? await TryGetTokenAsync(ct) : null;
        if (key is null && bearer is null)
        {
            throw new InvalidOperationException(
                "Azure refused to authenticate: no '" + ApiKeyName + "' secret is configured and no "
                + "managed identity or 'az login' credential was available.");
        }

        var url = $"{endpoint}/openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}";
        var body = new
        {
            messages = new[]
            {
                new { role = "system", content = InterpretationPrompt.System },
                new { role = "user", content = InterpretationPrompt.User(stats) },
            },
            // max_completion_tokens, not max_tokens: the reasoning-capable deployments reject the
            // older field outright. Set generously because the budget covers the model's internal
            // reasoning as well as the prose, and a tight cap returns an empty answer rather than a
            // short one — the same failure mode the local tier hit with a thinking model.
            max_completion_tokens = 4000,
        };

        using var client = httpClientFactory.CreateClient(nameof(AzureOpenAiSongInterpreter));
        using var response = await ResilientHttp.SendAsync(
            client,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
                if (key is not null)
                {
                    request.Headers.Add("api-key", key);
                }
                else
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }
                return request;
            },
            time, logger, ct);

        if (!response.IsSuccessStatusCode)
        {
            // The status only; an Azure error body can echo request content, and this request
            // carries the whole prompt.
            throw new InvalidOperationException(
                $"Azure refused the request ({(int)response.StatusCode}) for deployment '{deployment}'.");
        }

        var payload = await response.Content.ReadAsStringAsync(ct);
        var text = ReadContent(payload);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                $"Azure returned an empty interpretation for deployment '{deployment}' — the model "
                + "most likely spent its whole token budget on reasoning.");
        }

        // Token counts are logged because this is the only billed thing the app does, and the cost of
        // a press of Interpret is not otherwise visible until the invoice. Reasoning tokens are called
        // out separately: they are billed as output but never reach the reader, so a model that thinks
        // a lot can cost several times what its visible answer suggests.
        var (prompt, completion, reasoning) = ReadUsage(payload);
        logger.LogInformation(
            "Interpreted song statistics with Azure deployment {Deployment} using {Auth}. "
            + "Tokens: {Prompt} in, {Completion} out ({Reasoning} of them reasoning).",
            deployment, key is not null ? "an API key" : "a managed identity token",
            prompt, completion, reasoning);
        return text.Trim();
    }

    /// <summary>
    /// A cached data-plane token, or null when no credential in the chain can produce one. Never
    /// throws: an absent credential is an ordinary "this tier is not available here", not an error.
    /// </summary>
    private async Task<string?> TryGetTokenAsync(CancellationToken ct)
    {
        // Azure.Identity caches internally, but a still-valid token avoids even entering the chain.
        // Five minutes of headroom so a token cannot expire between the probe and the request.
        if (_token.Token is { Length: > 0 } && _token.ExpiresOn > time.GetUtcNow().AddMinutes(5))
        {
            return _token.Token;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TokenTimeout);

            _token = await Credential.GetTokenAsync(new TokenRequestContext([TokenScope]), timeout.Token);
            return _token.Token;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No Azure credential is available for the cloud interpreter.");
            return null;
        }
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Token counts from the response's usage block, zeroed when the service omits it. Never throws:
    /// a missing count must cost a log line's detail, never the interpretation the user waited for.
    /// </summary>
    private static (int Prompt, int Completion, int Reasoning) ReadUsage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("usage", out var usage))
            {
                return (0, 0, 0);
            }

            var reasoning = usage.TryGetProperty("completion_tokens_details", out var details)
                && details.TryGetProperty("reasoning_tokens", out var reasoningTokens)
                    ? reasoningTokens.GetInt32()
                    : 0;

            return (
                usage.TryGetProperty("prompt_tokens", out var prompt) ? prompt.GetInt32() : 0,
                usage.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : 0,
                reasoning);
        }
        catch (JsonException)
        {
            return (0, 0, 0);
        }
    }

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
