using System.Net.Http.Json;
using System.Text.Json;
using PoMode.API.Infrastructure;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// The local LLM tier: talks to an Ollama server on the user's own machine, so nothing about the
/// song leaves it and nothing is billed.
///
/// <para>Ollama rather than an in-process ONNX model on purpose. The runtime, the model download,
/// the quantisation and the GPU offload are all Ollama's problem, which keeps this repo free of a
/// multi-gigabyte catalog entry and a GenAI dependency for a feature that is a nicety, not the
/// product. The cost is an external process the user installs themselves — hence
/// <see cref="IsAvailableAsync"/> probing honestly and the tier simply disappearing when it is
/// absent, exactly like an undownloaded ONNX model.</para>
///
/// <para>Disabled in Azure mode for the same reason local ONNX models are: the hosted instance has
/// no localhost model server, and probing one on every request would just add latency.</para>
/// </summary>
public sealed class OllamaSongInterpreter(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<OllamaSongInterpreter> logger) : ISongInterpreter
{
    /// <summary>Ollama's default listen address. Overridable for a box that runs it elsewhere.</summary>
    private const string DefaultEndpoint = "http://localhost:11434";

    /// <summary>
    /// Preferred when it happens to be installed: small, fast, and ample for a task that is short and
    /// entirely grounded in its prompt. Only a preference — see <see cref="ResolveModelAsync"/>, which
    /// falls back to whatever the user actually has rather than insisting on this name.
    /// </summary>
    private const string PreferredModel = "llama3.2";

    /// <summary>
    /// The probe must not stall a page load, but it must survive the process's first HTTP request:
    /// handler setup made a 2 s budget fail on the very first probe and report a running Ollama as
    /// absent. When Ollama is genuinely missing the connection is refused immediately, so this
    /// budget is only ever spent on a server that is actually there.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Generation on a local model over a short prompt; generous, but not unbounded.</summary>
    private static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The model the last probe settled on. Cached so <see cref="InterpretAsync"/> runs the same model
    /// the availability check approved instead of listing tags a second time.
    /// </summary>
    private string? _resolvedModel;

    public string Name => nameof(OllamaSongInterpreter);

    public ExecutionTier Tier => ExecutionTier.Local;

    public bool UsesLocalModel => true;

    private string Endpoint =>
        (configuration["Llm:Ollama:Endpoint"] is { Length: > 0 } configured ? configured : DefaultEndpoint)
            .TrimEnd('/');

    /// <summary>The pinned model name, or null to let <see cref="ResolveModelAsync"/> choose.</summary>
    private string? ConfiguredModel =>
        configuration["Llm:Ollama:Model"] is { Length: > 0 } configured ? configured : null;

    /// <summary>True when Ollama answers and has a model this can run.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
        => await ResolveModelAsync(ct) is not null;

    /// <summary>
    /// Decides which installed model to use, and doubles as the availability probe.
    ///
    /// <para>A pinned <c>Llm:Ollama:Model</c> is honoured strictly: asking for a specific model and
    /// silently getting a different one would make results irreproducible. With nothing pinned this
    /// takes what Ollama actually has — preferring <see cref="PreferredModel"/>, else the first
    /// installed model. Insisting on a hard-coded name instead would leave the whole local tier dark
    /// for a user running Ollama with a perfectly good model under a different name.</para>
    ///
    /// <para>Returns null — never throws — when Ollama is absent, unreachable or empty. The tier is
    /// optional, so "not there" is an ordinary answer rather than an error.</para>
    /// </summary>
    private async Task<string?> ResolveModelAsync(CancellationToken ct)
    {
        if (EnvironmentDetector.IsAzureHosted())
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);

            using var client = httpClientFactory.CreateClient(nameof(OllamaSongInterpreter));
            using var response = await client.GetAsync($"{Endpoint}/api/tags", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var installed = InstalledModels(await response.Content.ReadAsStringAsync(timeout.Token));
            if (installed.Count == 0)
            {
                logger.LogInformation(
                    "Ollama is running but has no models installed; run 'ollama pull {Preferred}' to "
                    + "enable the local interpreter.", PreferredModel);
                return _resolvedModel = null;
            }

            if (ConfiguredModel is { } pinned)
            {
                var match = installed.FirstOrDefault(model => SameModel(model, pinned));
                if (match is null)
                {
                    logger.LogInformation(
                        "Ollama does not have the pinned model '{Model}' (installed: {Installed}). Run "
                        + "'ollama pull {Model}', or clear Llm:Ollama:Model to use whatever is installed.",
                        pinned, string.Join(", ", installed), pinned);
                }
                return _resolvedModel = match;
            }

            var chosen = installed.FirstOrDefault(model => SameModel(model, PreferredModel)) ?? installed[0];
            if (!string.Equals(_resolvedModel, chosen, StringComparison.Ordinal))
            {
                logger.LogInformation("Local interpreter will use Ollama model {Model}.", chosen);
            }
            return _resolvedModel = chosen;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // Nothing listening, or something that is not Ollama. Not an error: the tier is optional.
            return null;
        }
    }

    public async Task<string> InterpretAsync(SongStats stats, CancellationToken ct)
    {
        // Normally already settled by the selector's availability probe; resolved here as well so a
        // direct caller cannot reach the request with no model name.
        var model = _resolvedModel ?? await ResolveModelAsync(ct)
            ?? throw new InvalidOperationException("No Ollama model is available on this machine.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GenerateTimeout);

        using var client = httpClientFactory.CreateClient(nameof(OllamaSongInterpreter));
        client.Timeout = GenerateTimeout;

        var body = new
        {
            model,
            stream = false,
            // Reasoning models (gemma4, deepseek-r1, qwen3 ...) otherwise spend their whole output
            // budget on the "thinking" field and return an EMPTY "content" — observed with
            // gemma4:26b, which failed this call every time until thinking was switched off. The task
            // needs no reasoning: the arithmetic is done and the prompt states every fact. Ollama
            // accepts think:false on models without thinking support, so this is safe to send always.
            think = false,
            messages = new[]
            {
                new { role = "system", content = InterpretationPrompt.System },
                new { role = "user", content = InterpretationPrompt.User(stats) },
            },
            // Low but not zero: the wording may vary, the facts come from the prompt either way.
            options = new { temperature = 0.4 },
        };

        using var response = await client.PostAsync(
            $"{Endpoint}/api/chat", JsonContent.Create(body), timeout.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama refused the request ({(int)response.StatusCode}) for model '{model}'.");
        }

        var payload = await response.Content.ReadAsStringAsync(timeout.Token);
        var text = ReadContent(payload);
        if (string.IsNullOrWhiteSpace(text))
        {
            // Naming the likely cause: an empty answer from a reasoning model almost always means it
            // reasoned instead of replying, which points at a model choice rather than a bug here.
            throw new InvalidOperationException(
                $"Ollama returned an empty interpretation for model '{model}'"
                + (HasThinking(payload)
                    ? " — it produced reasoning but no answer. Try a non-reasoning model, e.g. 'ollama pull llama3.2'."
                    : "."));
        }

        logger.LogInformation("Interpreted song statistics locally with Ollama model {Model}.", model);
        return text.Trim();
    }

    /// <summary>Installed model names from <c>/api/tags</c>, tags included ("gemma4:26b").</summary>
    private static List<string> InstalledModels(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. models.EnumerateArray()
            .Select(model => model.TryGetProperty("name", out var name) ? name.GetString() : null)
            .OfType<string>()];
    }

    /// <summary>
    /// "llama3.2" and "llama3.2:latest" name the same model to a user, so a pin without a tag matches
    /// any tag. A pin that does carry a tag is matched exactly — the point of writing one is to choose.
    /// </summary>
    private static bool SameModel(string installed, string wanted)
        => wanted.Contains(':', StringComparison.Ordinal)
            ? installed.Equals(wanted, StringComparison.OrdinalIgnoreCase)
            : BaseName(installed).Equals(wanted, StringComparison.OrdinalIgnoreCase);

    private static string BaseName(string model)
    {
        var separator = model.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? model : model[..separator];
    }

    private static string? ReadContent(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
                ? content.GetString()
                : null;
    }

    /// <summary>True when the reply carried chain-of-thought — used only to explain an empty answer.</summary>
    private static bool HasThinking(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("message", out var message)
            && message.TryGetProperty("thinking", out var thinking)
            && !string.IsNullOrWhiteSpace(thinking.GetString());
    }
}
