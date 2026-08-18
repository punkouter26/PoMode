using System.Net.Http.Json;
using System.Text.Json;
using PoMode.API.Features.Cloud;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.StemSeparation;

/// <summary>
/// Tier-3 stem separation via LALAL.AI (spec §4: paid, last resort), the second cloud provider so a
/// single provider outage does not sink the tier.
///
/// Protocol taken from LALAL.AI's official client: <c>upload/</c> → <c>split/stem_separator/</c> →
/// poll <c>check/</c> → download tracks → <c>delete/</c>. Authentication is the <c>X-License-Key</c>
/// header, not a bearer token.
///
/// <para>The uploaded source is deleted in a <c>finally</c>, so a failed or cancelled split never
/// leaves the user's audio sitting on a third-party server.</para>
/// </summary>
public sealed class LalalStemSeparator(
    CloudCredentials credentials,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<LalalStemSeparator> logger) : IStemSeparator
{
    private const string KeyName = "LalalApiKey";
    private const string DefaultBaseUrl = "https://www.lalal.ai/api/v1";
    private const int DefaultMaxUploadMb = 100;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollBudget = TimeSpan.FromMinutes(20);

    private static readonly string[] VocalLabels = ["vocals", "vocal"];
    private static readonly string[] InstrumentalLabels =
        ["instrumental", "no_vocals", "accompaniment", "backing", "back_track"];

    public string Name => nameof(LalalStemSeparator);
    public ExecutionTier Tier => ExecutionTier.Cloud;

    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => Task.FromResult(credentials.Has(KeyName));

    public async Task SeparateAsync(StageContext context, CancellationToken ct)
    {
        var key = credentials.TokenFor(KeyName)
            ?? throw new InvalidOperationException("No LALAL.AI licence key is configured.");
        var baseUrl = (configuration["Cloud:Lalal:BaseUrl"] ?? DefaultBaseUrl).TrimEnd('/');

        var maxMb = configuration.GetValue("Cloud:MaxUploadMb", DefaultMaxUploadMb);
        var length = new FileInfo(context.InputPath).Length;
        if (length > (long)maxMb * 1024 * 1024)
        {
            throw new InvalidOperationException(
                $"This track is too large to send to LALAL.AI ({length / 1024 / 1024} MB; the limit is {maxMb} MB).");
        }

        using var client = httpClientFactory.CreateClient("lalal");
        var sourceId = await UploadAsync(client, baseUrl, key, context.InputPath, ct);
        try
        {
            var taskId = await StartSplitAsync(client, baseUrl, key, sourceId, ct);
            var tracks = await PollAsync(client, baseUrl, key, taskId, ct);

            var vocalsUrl = TrackUrl(tracks, VocalLabels)
                ?? throw new InvalidOperationException(
                    "LALAL.AI returned no vocals track that this executor recognises.");
            var instrumentalUrl = TrackUrl(tracks, InstrumentalLabels)
                ?? throw new InvalidOperationException(
                    "LALAL.AI returned no instrumental track that this executor recognises.");

            // Independent URLs and destination files — download both stems concurrently.
            await Task.WhenAll(
                StemDownloader.DownloadStemAsync(
                    client, vocalsUrl, Path.Combine(context.JobDir, "vocals.wav"), "LALAL.AI", time, logger, ct),
                StemDownloader.DownloadStemAsync(
                    client, instrumentalUrl, Path.Combine(context.JobDir, "instrumental.wav"), "LALAL.AI", time, logger, ct));
        }
        finally
        {
            // Best effort, and deliberately not allowed to mask the real failure above.
            await TryDeleteAsync(client, baseUrl, key, sourceId);
        }
    }

    private async Task<string> UploadAsync(
        HttpClient client, string baseUrl, string key, string inputPath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(inputPath, ct);
        var fileName = Path.GetFileName(inputPath);

        using var response = await ResilientHttp.SendAsync(
            client,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/upload/")
                {
                    Content = new ByteArrayContent(bytes),
                };
                request.Headers.Add("X-License-Key", key);
                request.Content.Headers.Add("Content-Disposition", $"attachment; filename=\"{fileName}\"");
                return request;
            },
            time, logger, ct);

        var payload = await ReadAsync(response, "upload the track", ct);
        return payload.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } sourceId
            ? sourceId
            : throw new InvalidOperationException("LALAL.AI did not return a source id for the upload.");
    }

    private async Task<string> StartSplitAsync(
        HttpClient client, string baseUrl, string key, string sourceId, CancellationToken ct)
    {
        var body = new
        {
            source_id = sourceId,
            presets = new { stems = new[] { "vocals" } },
        };

        using var response = await ResilientHttp.SendAsync(
            client,
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/split/stem_separator/")
                {
                    Content = JsonContent.Create(body),
                };
                request.Headers.Add("X-License-Key", key);
                return request;
            },
            time, logger, ct);

        var payload = await ReadAsync(response, "start the split", ct);
        return payload.TryGetProperty("task_id", out var id) && id.GetString() is { Length: > 0 } taskId
            ? taskId
            : throw new InvalidOperationException("LALAL.AI did not return a task id for the split.");
    }

    /// <summary>Polls <c>check/</c> until the task succeeds, returning its track array.</summary>
    private async Task<JsonElement> PollAsync(
        HttpClient client, string baseUrl, string key, string taskId, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + PollBudget;
        while (true)
        {
            using var response = await ResilientHttp.SendAsync(
                client,
                () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/check/")
                    {
                        Content = JsonContent.Create(new { task_ids = new[] { taskId } }),
                    };
                    request.Headers.Add("X-License-Key", key);
                    return request;
                },
                time, logger, ct);

            var payload = await ReadAsync(response, "check the split", ct);
            if (!payload.TryGetProperty("result", out var results)
                || !results.TryGetProperty(taskId, out var task))
            {
                throw new InvalidOperationException($"LALAL.AI reported nothing for task {taskId}.");
            }

            var status = task.TryGetProperty("status", out var element) ? element.GetString() : null;
            if (status == "success")
            {
                // Their client reads tracks under a nested "result"; tolerate a flat shape too.
                if (task.TryGetProperty("result", out var inner)
                    && inner.TryGetProperty("tracks", out var nestedTracks))
                {
                    return nestedTracks;
                }
                return task.TryGetProperty("tracks", out var flatTracks)
                    ? flatTracks
                    : throw new InvalidOperationException("LALAL.AI reported success with no tracks.");
            }
            if (status is "cancelled" or "canceled" or "error" or "failed")
            {
                throw new InvalidOperationException($"The LALAL.AI split was {status}.");
            }

            if (time.GetUtcNow() > deadline)
            {
                throw new InvalidOperationException("The LALAL.AI split did not finish in time.");
            }
            await Task.Delay(PollInterval, time, ct);
        }
    }

    private static string? TrackUrl(JsonElement tracks, string[] labels)
    {
        if (tracks.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var label in labels)
        {
            foreach (var track in tracks.EnumerateArray())
            {
                var trackLabel = track.TryGetProperty("label", out var element) ? element.GetString() : null;
                if (string.Equals(trackLabel, label, StringComparison.OrdinalIgnoreCase)
                    && track.TryGetProperty("url", out var url)
                    && url.GetString() is { Length: > 0 } value)
                {
                    return value;
                }
            }
        }
        return null;
    }

    private async Task TryDeleteAsync(HttpClient client, string baseUrl, string key, string sourceId)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/delete/")
            {
                Content = JsonContent.Create(new { source_id = sourceId }),
            };
            request.Headers.Add("X-License-Key", key);
            using var response = await client.SendAsync(request, CancellationToken.None);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LALAL.AI would not delete source {SourceId} ({Status}).",
                    sourceId, (int)response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Could not delete LALAL.AI source {SourceId}.", sourceId);
        }
    }

    private static async Task<JsonElement> ReadAsync(
        HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"LALAL.AI refused to {action} ({(int)response.StatusCode}).");
        }
        return StemDownloader.ParseJson(await response.Content.ReadAsStringAsync(ct));
    }
}
