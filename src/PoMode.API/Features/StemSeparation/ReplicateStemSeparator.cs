using System.Net.Http.Json;
using System.Text.Json;
using PoMode.API.Features.Cloud;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.StemSeparation;

/// <summary>
/// Tier-3 stem separation via Replicate (spec §4: paid, last resort). Reached only when every local
/// separator is unavailable or has thrown, and the recorded plan then shows <c>Cloud</c> so the UI
/// marks the job ☁️💰 — a paid call is never invisible.
///
/// Protocol verified against Replicate's documentation: create a prediction, poll <c>urls.get</c> until
/// <c>status</c> is terminal, then read the stem URLs out of <c>output</c>. The terminal success value
/// is <b><c>succeeded</c></b>; one published doc summary calls it "successful", which would make every
/// prediction look unfinished forever.
/// </summary>
public sealed class ReplicateStemSeparator(
    CloudCredentials credentials,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<ReplicateStemSeparator> logger) : IStemSeparator
{
    private const string TokenKey = "ReplicateApiToken";
    private const string DefaultBaseUrl = "https://api.replicate.com";
    private const string DefaultModel = "cjwbw/demucs";
    private const int DefaultMaxUploadMb = 25;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    /// <summary>Replicate times predictions out server-side at 30 minutes; stop a little after.</summary>
    private static readonly TimeSpan PollBudget = TimeSpan.FromMinutes(31);

    /// <summary>Keys demucs variants use for the two stems we need.</summary>
    private static readonly string[] VocalKeys = ["vocals", "vocal"];
    private static readonly string[] InstrumentalKeys =
        ["instrumental", "no_vocals", "accompaniment", "other", "backing"];

    public string Name => nameof(ReplicateStemSeparator);
    public ExecutionTier Tier => ExecutionTier.Cloud;

    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => Task.FromResult(credentials.Has(TokenKey));

    public async Task SeparateAsync(StageContext context, CancellationToken ct)
    {
        var token = credentials.TokenFor(TokenKey)
            ?? throw new InvalidOperationException("No Replicate API token is configured.");
        var baseUrl = (configuration["Cloud:Replicate:BaseUrl"] ?? DefaultBaseUrl).TrimEnd('/');
        var model = configuration["Cloud:Replicate:StemModel"] ?? DefaultModel;

        var inputUri = BuildDataUri(context.InputPath);

        using var client = httpClientFactory.CreateClient("replicate");
        var prediction = await CreateAsync(client, baseUrl, model, token, inputUri, ct);
        var output = await PollAsync(client, prediction, token, ct);

        var vocalsUrl = ResolveStemUrl(output, VocalKeys, index: 0)
            ?? throw new InvalidOperationException(
                "The Replicate prediction returned no vocals stem URL that this executor recognises.");
        var instrumentalUrl = ResolveStemUrl(output, InstrumentalKeys, index: 1)
            ?? throw new InvalidOperationException(
                "The Replicate prediction returned no instrumental stem URL that this executor recognises.");

        // Independent URLs and destination files — download both stems concurrently.
        await Task.WhenAll(
            StemDownloader.DownloadStemAsync(
                client, vocalsUrl, Path.Combine(context.JobDir, "vocals.wav"), "Replicate", time, logger, ct),
            StemDownloader.DownloadStemAsync(
                client, instrumentalUrl, Path.Combine(context.JobDir, "instrumental.wav"), "Replicate", time, logger, ct));
    }

    /// <summary>
    /// Replicate needs a fetchable URL for the audio, and a local job file has none, so the bytes
    /// travel as a data URI. That makes the request body grow with the file, hence the size guard.
    /// </summary>
    private string BuildDataUri(string inputPath)
    {
        var maxMb = configuration.GetValue("Cloud:MaxUploadMb", DefaultMaxUploadMb);
        var length = new FileInfo(inputPath).Length;
        if (length > (long)maxMb * 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"This track is too large to send to Replicate ({length / 1024 / 1024} MB; the limit is {maxMb} MB).");
        }

        var mediaType = Path.GetExtension(inputPath).ToLowerInvariant() == ".mp3" ? "audio/mpeg" : "audio/wav";
        return $"data:{mediaType};base64,{Convert.ToBase64String(File.ReadAllBytes(inputPath))}";
    }

    /// <summary>
    /// <c>owner/name</c> runs the model's default version through the models endpoint;
    /// <c>owner/name:hash</c> pins a version through the predictions endpoint. Supporting both means no
    /// version hash has to be hard-coded, which would rot.
    /// </summary>
    private async Task<JsonElement> CreateAsync(
        HttpClient client, string baseUrl, string model, string token, string inputUri, CancellationToken ct)
    {
        var separator = model.IndexOf(':', StringComparison.Ordinal);
        var input = new { audio = inputUri, stem = "vocals" };
        var (url, body) = separator < 0
            ? ($"{baseUrl}/v1/models/{model}/predictions", (object)new { input })
            : ($"{baseUrl}/v1/predictions", new { version = model[(separator + 1)..], input });

        using var response = await ResilientHttp.SendAsync(
            client,
            () => new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(body),
                Headers = { { "Authorization", $"Bearer {token}" } },
            },
            time, logger, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Replicate refused to create a prediction ({(int)response.StatusCode}).");
        }
        return StemDownloader.ParseJson(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JsonElement> PollAsync(
        HttpClient client, JsonElement prediction, string token, CancellationToken ct)
    {
        var pollUrl = prediction.TryGetProperty("urls", out var urls)
            && urls.TryGetProperty("get", out var get)
            && get.GetString() is { Length: > 0 } fromPayload
                ? fromPayload
                : throw new InvalidOperationException("Replicate did not return a prediction poll URL.");

        var current = prediction;
        var deadline = time.GetUtcNow() + PollBudget;
        while (true)
        {
            var status = current.TryGetProperty("status", out var element) ? element.GetString() : null;
            switch (status)
            {
                case "succeeded":
                    return current.TryGetProperty("output", out var output)
                        ? output
                        : throw new InvalidOperationException("Replicate reported success with no output.");
                case "failed":
                case "canceled":
                    var error = current.TryGetProperty("error", out var errorElement)
                        ? errorElement.GetString()
                        : null;
                    throw new InvalidOperationException(
                        $"The Replicate prediction {status}{(error is null ? "." : $": {error}")}");
            }

            if (time.GetUtcNow() > deadline)
            {
                throw new InvalidOperationException("The Replicate prediction did not finish in time.");
            }

            await Task.Delay(PollInterval, time, ct);
            using var response = await ResilientHttp.SendAsync(
                client,
                () => new HttpRequestMessage(HttpMethod.Get, pollUrl)
                {
                    Headers = { { "Authorization", $"Bearer {token}" } },
                },
                time, logger, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Replicate refused to report prediction status ({(int)response.StatusCode}).");
            }
            current = StemDownloader.ParseJson(await response.Content.ReadAsStringAsync(ct));
        }
    }

    /// <summary>
    /// <c>output</c> is model-shaped: some demucs variants return an object keyed by stem, others a
    /// bare array of URLs. Accept both rather than assuming one and failing opaquely on the other.
    /// </summary>
    private static string? ResolveStemUrl(JsonElement output, string[] keys, int index)
    {
        if (output.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in keys)
            {
                if (output.TryGetProperty(key, out var element) && element.GetString() is { Length: > 0 } url)
                {
                    return url;
                }
            }
            return null;
        }
        if (output.ValueKind == JsonValueKind.Array)
        {
            var urls = output.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString()!)
                .ToArray();
            return index < urls.Length ? urls[index] : null;
        }
        return output.ValueKind == JsonValueKind.String && index == 0 ? output.GetString() : null;
    }

}
