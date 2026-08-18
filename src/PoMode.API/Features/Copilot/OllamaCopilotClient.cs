using System.Net.Http.Json;
using System.Text.Json;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Infrastructure;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Copilot;

/// <summary>
/// Explains one analysis window using a locally installed Ollama model (spec §5).
///
/// Two rules this type exists to enforce. First, it is **always optional**: no Ollama, no model, a
/// timeout or a malformed answer all produce <c>Available = false</c> with a reason, never an
/// exception and never a failed request. Second, it is **never cloud**: the base URL must resolve to
/// loopback, so no configuration change can quietly turn the copilot into a paid API call.
/// </summary>
public sealed class OllamaCopilotClient(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<OllamaCopilotClient> logger)
{
    /// <summary>
    /// Spec §5's preference order. §13.1 recorded that none of these is actually installed on the dev
    /// machine (it has <c>gemma4:26b</c>), so the list is a *preference* and the client falls through
    /// to the first installed model rather than giving up — that way it works on any box.
    /// </summary>
    private static readonly string[] PreferredModels = ["qwen2.5:7b", "llama3.3:8b", "llama3.2:3b"];

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long one generate call may take, configurable for slow machines: a large local model
    /// (the dev box runs gemma4:26b) can spend most of a minute just loading into memory on the
    /// first call. Capped at 95 s because the Blazor client's default HttpClient gives up at 100 s
    /// — a server that waits longer only produces a client-side failure instead of a clean reason.
    /// </summary>
    private TimeSpan GenerateTimeout => TimeSpan.FromSeconds(
        Math.Clamp(configuration.GetValue("Copilot:GenerateTimeoutSeconds", 90), 5, 95));

    /// <summary>
    /// The reply is specified as exactly two sentences, so cap generation — an unbounded answer
    /// from a big slow model is the difference between "slow" and "timed out".
    /// </summary>
    private const int MaxAnswerTokens = 220;

    /// <summary>Keep the model loaded between requests: the first answer pays the load cost once,
    /// then Regenerate and later windows answer fast instead of re-loading a many-GB model.</summary>
    private const string KeepAlive = "15m";

    /// <summary>Bounds how many unusable models the user waits through before we give up.</summary>
    private const int MaxModelAttempts = 3;

    public async Task<CopilotReply> ExplainAsync(ModalResult result, ModalWindow window, CancellationToken ct)
    {
        if (!TryGetLoopbackBaseUrl(out var baseUrl, out var rejection))
        {
            logger.LogWarning("Copilot base URL rejected: {Reason}", rejection);
            return new CopilotReply(false, null, null, rejection);
        }

        try
        {
            var candidates = await CandidateModelsAsync(baseUrl, ct);
            if (candidates.Count == 0)
            {
                return new CopilotReply(false, null, null,
                    "Ollama is not running, or it has no models installed.");
            }

            var prompt = BuildPrompt(result, window);
            CopilotReply? lastFailure = null;
            foreach (var model in candidates.Take(MaxModelAttempts))
            {
                var attempt = await GenerateAsync(baseUrl, model, prompt, ct);
                if (attempt.Available)
                {
                    return attempt;
                }
                // A model can be installed and still unusable — an unloadable 26B model on a small
                // machine answers 500 ("failed to allocate CPU buffer"). Fall through to the next
                // candidate instead of reporting the copilot as unavailable.
                logger.LogInformation("Copilot model {Model} unusable: {Reason}", model, attempt.Reason);
                lastFailure = attempt;
            }
            return lastFailure ?? new CopilotReply(false, null, null, "No installed model could answer.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            logger.LogInformation(ex, "Copilot unavailable");
            return new CopilotReply(false, null, null, "Could not reach Ollama on this machine.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CopilotReply(false, null, null,
                "Ollama did not answer in time — a large model may still be loading. Try Regenerate; the model stays loaded after its first answer.");
        }
    }

    private async Task<CopilotReply> GenerateAsync(
        string baseUrl, string model, string prompt, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("ollama");
        client.Timeout = GenerateTimeout;
        using var response = await client.PostAsJsonAsync(
            $"{baseUrl}/api/generate",
            new { model, prompt, stream = false, keep_alive = KeepAlive, options = new { num_predict = MaxAnswerTokens } },
            ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        string? error = null;
        string? text = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                error = errorElement.GetString();
            }
            if (document.RootElement.TryGetProperty("response", out var responseElement))
            {
                text = responseElement.GetString();
            }
        }
        catch (JsonException)
        {
            return new CopilotReply(false, null, model, "Ollama returned a malformed answer.");
        }

        if (!response.IsSuccessStatusCode)
        {
            // Ollama puts the real cause in an `error` field; surfacing it beats a bare status code.
            return new CopilotReply(false, null, model,
                error is null
                    ? $"Ollama answered {(int)response.StatusCode}."
                    : $"Ollama answered {(int)response.StatusCode}: {Summarise(error)}");
        }

        return string.IsNullOrWhiteSpace(text)
            ? new CopilotReply(false, null, model, "Ollama returned an empty answer.")
            : new CopilotReply(true, text.Trim(), model, null);
    }

    /// <summary>Ollama's errors can be multi-line dumps; keep the reason short enough for a UI card.</summary>
    private static string Summarise(string error)
    {
        var firstLine = error.Split('\n', StringSplitOptions.TrimEntries)[0];
        return firstLine.Length <= 160 ? firstLine : firstLine[..160] + "…";
    }

    /// <summary>Installed models, preference order first, then whatever else is there.</summary>
    private async Task<IReadOnlyList<string>> CandidateModelsAsync(string baseUrl, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient("ollama");
        client.Timeout = ListTimeout;
        var installed = await OllamaEndpoint.ListInstalledModelsAsync(client, baseUrl, ct);

        // Spec-preferred models first, in spec order, then everything else in Ollama's own order.
        return [.. PreferredModels.Where(installed.Contains), .. installed.Except(PreferredModels)];
    }

    private bool TryGetLoopbackBaseUrl(out string baseUrl, out string? rejection)
        => OllamaEndpoint.TryResolveLoopbackBaseUrl(configuration, out baseUrl, out rejection);

    private static string BuildPrompt(ModalResult result, ModalWindow window)
    {
        var top = window.Matches.Count > 0 ? window.Matches[0] : null;
        var mode = window.InsufficientEvidence || top is null
            ? "no confident match (too few distinct sung notes)"
            : $"{top.Mode} at {top.Confidence:P0} confidence";
        var intervals = window.SungIntervals.Count == 0
            ? "none"
            : string.Join(", ", window.SungIntervals.Select(PitchNames.IntervalLabel));

        return $"""
            You are a music theory assistant explaining an automated modal analysis to a musician.

            Chord sounding now: {window.ChordSymbol} (measure {window.MeasureNumber})
            Song key centre: {result.TonicName}
            Sung scale degrees over this chord: {intervals}
            Sung-degree bitmask: 0x{window.VocalMask:X3}
            Detected mode: {mode}

            In exactly two sentences, explain what this combination means musically and why the detected
            mode fits or does not fit. Be concrete about the scale degrees. Do not add a preamble, a
            heading, or a list. Plain prose, with Markdown emphasis only if it helps.
            """;
    }
}
