using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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
    /// Preferred when it happens to be installed, on the numbers: in the interpreter report
    /// (<c>test-reports/interpreter-report.html</c>, 2026-09-25) gemma3:4b passed all five tasks —
    /// schema, no invented figures, declining the unanswerable question — on both runs, before and
    /// after the prompts were restructured for prefix reuse. llama3.2 passed three on the first (it
    /// looped past the length cap on the summary and invented a figure in an answer) and five on the
    /// second, a little faster; consistency won. Only a preference — see
    /// <see cref="ResolveModelAsync"/>, which falls back to whatever the user actually has rather than
    /// insisting on this name.
    /// </summary>
    public const string PreferredModel = "gemma3";

    /// <summary>
    /// The context window asked for. Set explicitly because Ollama's default is smaller than a
    /// question prompt can grow (the full statistics block restated, plus six exchanges), and Ollama
    /// does not refuse an overlong prompt: it drops the <em>front</em> of it — which is exactly where
    /// the measurements sit. <see cref="ReplyAsync"/> logs a warning when a prompt fills it anyway.
    /// </summary>
    private const int ContextTokens = 8192;

    /// <summary>
    /// The longest reply allowed, in tokens. A summary is six short paragraphs, well under half of
    /// this. The cap exists for the failure it stops: under a JSON constraint a small model can fall
    /// into repeating its last sentences and never close the string — llama3.2 wrote 12,000
    /// characters of theory that way and ran into the five-minute timeout. A reply that hits the cap
    /// is treated as failed, so the selector falls through in a minute instead of five.
    /// </summary>
    private const int MaxReplyTokens = 1536;

    /// <summary>
    /// The probe must not stall a page load, but it must survive the process's first HTTP request:
    /// handler setup made a 2 s budget fail on the very first probe and report a running Ollama as
    /// absent. When Ollama is genuinely missing the connection is refused immediately, so this
    /// budget is only ever spent on a server that is actually there.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Generation on a local model over a short prompt; generous, but not unbounded.</summary>
    private static readonly TimeSpan GenerateTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How often a probe may ask Ollama to load the model ahead of a request.</summary>
    private static readonly TimeSpan WarmInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The model the last probe settled on. Cached so <see cref="ReplyAsync"/> runs the same model
    /// the availability check approved instead of listing tags a second time.
    /// </summary>
    private string? _resolvedModel;

    private long _warmedAt;

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
                    "Ollama is running but has no models installed; run 'ollama pull {Preferred}:4b' to "
                    + "enable the local interpreter.", PreferredModel);
                return _resolvedModel = null;
            }

            string? chosen;
            if (ConfiguredModel is { } pinned)
            {
                chosen = installed.FirstOrDefault(model => SameModel(model, pinned));
                if (chosen is null)
                {
                    logger.LogInformation(
                        "Ollama does not have the pinned model '{Model}' (installed: {Installed}). Run "
                        + "'ollama pull {Model}', or clear Llm:Ollama:Model to use whatever is installed.",
                        pinned, string.Join(", ", installed), pinned);
                }
            }
            else
            {
                chosen = installed.FirstOrDefault(model => SameModel(model, PreferredModel)) ?? installed[0];
                if (!string.Equals(_resolvedModel, chosen, StringComparison.Ordinal))
                {
                    logger.LogInformation("Local interpreter will use Ollama model {Model}.", chosen);
                }
            }

            if (chosen is not null)
            {
                Warm(chosen);
            }
            return _resolvedModel = chosen;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            // Nothing listening, or something that is not Ollama. Not an error: the tier is optional.
            return null;
        }
    }

    /// <summary>
    /// Asks Ollama to load the model now, in the background. A probe runs when the analysis page
    /// lists its interpreters, which is the moment someone may be about to ask; loading a few GB of
    /// weights then rather than on the first question takes seconds off the first answer. Throttled,
    /// because a probe also runs before every request.
    ///
    /// <para>How long the model then stays resident is left to Ollama's own default (five minutes)
    /// on purpose. A 3–4 GB model held longer competes with stem separation's 5.7 GB peak on a
    /// 16 GB machine: a 30-minute keep-alive once left three models resident and HTDemucs failed to
    /// allocate.</para>
    /// </summary>
    private void Warm(string model)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _warmedAt);
        if (last != 0 && now - last < WarmInterval.TotalMilliseconds
            || Interlocked.CompareExchange(ref _warmedAt, now, last) != last)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = httpClientFactory.CreateClient(nameof(OllamaSongInterpreter));
                client.Timeout = GenerateTimeout;
                // A generate request with no prompt only loads the model — at the same context size
                // the real requests ask for. Loaded at Ollama's default instead, the first question
                // made it reload the model (87 s to the first word), and a request arriving
                // mid-reload got a 500.
                using var response = await client.PostAsync(
                    $"{Endpoint}/api/generate", JsonContent.Create(new { model, options = new { num_ctx = ContextTokens } }));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Best effort: the real request loads the model anyway.
            }
        });
    }

    /// <summary>
    /// One streamed, schema-constrained chat completion.
    ///
    /// <para>The whole conversation arrives as the prompt's messages — a question's measurements
    /// and transcript are already one flat user message, because the measurements have to lead every
    /// turn and one message is the only shape that guarantees they do.</para>
    /// </summary>
    public async IAsyncEnumerable<string> ReplyAsync(
        InterpretationRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        // Normally already settled by the selector's availability probe; resolved here as well so a
        // direct caller cannot reach the request with no model name.
        var model = _resolvedModel ?? await ResolveModelAsync(ct)
            ?? throw new InvalidOperationException("No Ollama model is available on this machine.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GenerateTimeout);

        using var client = httpClientFactory.CreateClient(nameof(OllamaSongInterpreter));
        client.Timeout = GenerateTimeout;

        var prompt = request.Prompt;
        using var schema = JsonDocument.Parse(prompt.Schema);
        var body = new
        {
            model,
            stream = true,
            // Reasoning models (gemma4, deepseek-r1, qwen3 ...) otherwise spend their whole output
            // budget on the "thinking" field and return an EMPTY "content" — observed with
            // gemma4:26b, which failed this call every time until thinking was switched off. The task
            // needs no reasoning: the arithmetic is done and the prompt states every fact. Ollama
            // accepts think:false on models without thinking support, so this is safe to send always.
            think = false,
            // Constrains decoding to the reply schema, so the output is always the two fields.
            format = schema.RootElement,
            messages = prompt.Messages
                .Select(message => new { role = message.Role, content = message.Content })
                .Prepend(new { role = "system", content = prompt.System }),
            // Low but not zero: the wording may vary, the facts come from the prompt either way.
            options = new { temperature = 0.4, num_ctx = ContextTokens, num_predict = MaxReplyTokens },
        };

        var clock = Stopwatch.StartNew();
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Endpoint}/api/chat")
        {
            Content = JsonContent.Create(body),
        };
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama refused the request ({(int)response.StatusCode}) for model '{model}': "
                + await response.Content.ReadAsStringAsync(timeout.Token));
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var firstToken = TimeSpan.Zero;
        var wrote = false;
        var thought = false;
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException($"Ollama failed for model '{model}': {error.GetString()}");
            }

            if (root.TryGetProperty("message", out var reply))
            {
                thought |= reply.TryGetProperty("thinking", out var thinking)
                    && !string.IsNullOrWhiteSpace(thinking.GetString());
                if (reply.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } text)
                {
                    if (!wrote)
                    {
                        firstToken = clock.Elapsed;
                        wrote = true;
                    }
                    yield return text;
                }
            }

            if (root.TryGetProperty("done", out var done) && done.GetBoolean())
            {
                LogUsage(root, model, firstToken, clock.Elapsed);
                if (root.TryGetProperty("done_reason", out var reason) && reason.GetString() == "length")
                {
                    throw new InvalidOperationException(
                        $"Ollama model '{model}' ran past {MaxReplyTokens} tokens without finishing its reply; "
                        + "it was most likely repeating itself.");
                }
            }
        }

        if (!wrote)
        {
            // Naming the likely cause: an empty answer from a reasoning model almost always means it
            // reasoned instead of replying, which points at a model choice rather than a bug here.
            throw new InvalidOperationException(
                $"Ollama returned an empty response for model '{model}'"
                + (thought
                    ? " — it produced reasoning but no answer. Try a non-reasoning model, e.g. 'ollama pull gemma3:4b'."
                    : "."));
        }
    }

    /// <summary>
    /// Timing and token counts from the final stream line. The prompt count is also the only way to
    /// see a silent truncation: a prompt that fills the context window has lost its beginning.
    /// </summary>
    private void LogUsage(JsonElement final, string model, TimeSpan firstToken, TimeSpan total)
    {
        var promptTokens = final.TryGetProperty("prompt_eval_count", out var p) ? p.GetInt32() : 0;
        var replyTokens = final.TryGetProperty("eval_count", out var e) ? e.GetInt32() : 0;
        logger.LogInformation(
            "Ollama {Model}: first token {FirstMs} ms, done in {TotalMs} ms, {PromptTokens} prompt + "
            + "{ReplyTokens} reply tokens.",
            model, (int)firstToken.TotalMilliseconds, (int)total.TotalMilliseconds, promptTokens, replyTokens);
        if (promptTokens >= ContextTokens)
        {
            logger.LogWarning(
                "Ollama {Model}: the prompt filled the {Context}-token context window, so Ollama dropped "
                + "its start — where the measurements are. The answer may not be grounded in them.",
                model, ContextTokens);
        }
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
    /// "gemma3" and "gemma3:latest" name the same model to a user, so a pin without a tag matches
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
}
