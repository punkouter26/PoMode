using System.Collections.Concurrent;
using System.Text.Json;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Reference;

/// <summary>
/// Looks an upload up in two public, free, unauthenticated catalogues: MusicBrainz for identity
/// (title, artist, release, length) and AcousticBrainz for the community's own key and tempo
/// estimates of that same recording.
///
/// <para>Both are optional in exactly the way the local Ollama tier is: absent, unreachable or silent
/// is an ordinary answer, never an error. The interesting thing this feature produces is a comparison,
/// and a comparison with nothing on the other side is simply not shown. AcousticBrainz in particular
/// stopped taking submissions in 2022 and serves a frozen dataset, so a miss there is the common case
/// rather than the exception — which is why <see cref="ReferenceLookupDto"/> distinguishes "no such
/// recording" from "the catalogue did not answer".</para>
///
/// <para>Nothing about the audio is sent anywhere. The only thing that leaves the machine is the
/// cleaned-up file name, because that is all a text search needs — there is no fingerprinting here
/// and no upload of the recording itself.</para>
/// </summary>
public sealed class MusicBrainzCatalog(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    TimeProvider time,
    ILogger<MusicBrainzCatalog> logger)
{
    private const string MusicBrainzEndpoint = "https://musicbrainz.org/ws/2";
    private const string AcousticBrainzEndpoint = "https://acousticbrainz.org/api/v1";
    private const string CoverArtEndpoint = "https://coverartarchive.org/release";

    /// <summary>MusicBrainz asks anonymous clients for no more than one request a second and for a
    /// User-Agent that identifies the application. Both are conditions of use, not suggestions.</summary>
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromSeconds(1.1);

    /// <summary>A page load should not sit on a third-party catalogue. Past this the answer is
    /// "unreachable", which the client shows as an absence.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a query's answer is reused. The catalogues change on the order of days and the
    /// library re-asks the same handful of questions every time a job is opened, so this exists to
    /// keep the app inside the rate limit rather than to make it fast.
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    /// <summary>Candidates to consider from a search. More than this and the tail is noise.</summary>
    private const int SearchLimit = 5;

    /// <summary>
    /// MusicBrainz scores a free-text match 0-100. Below this the "match" is usually one word of the
    /// title against a different song, and presenting it beside the user's analysis would invite them
    /// to trust a comparison against the wrong recording.
    /// </summary>
    private const int MinMatchScore = 70;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ReferenceLookupDto Result)> _cache = new();
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    /// <summary>Turned off by default in Azure-hosted mode is deliberately <em>not</em> done here:
    /// unlike a localhost model server, a public catalogue is reachable from anywhere. It can still be
    /// switched off outright for an air-gapped deployment.</summary>
    public bool Enabled => configuration.GetValue("Reference:Enabled", true);

    /// <summary>
    /// Identifies this application to MusicBrainz. Configurable so a deployment can put its own
    /// contact address in, which is what the catalogue actually asks for.
    /// </summary>
    private string UserAgent => configuration["Reference:UserAgent"] is { Length: > 0 } configured
        ? configured
        : "PoMode/1.0 ( https://github.com/punkouter26/PoMode )";

    /// <summary>
    /// The catalogue's view of one recording, compared against what this job measured.
    ///
    /// <para>Never throws for a network problem: a dead catalogue degrades the panel, not the page.</para>
    /// </summary>
    public async Task<ReferenceLookupDto> LookupAsync(
        string query, ModalResult? measured, CancellationToken ct)
    {
        if (!Enabled)
        {
            return new ReferenceLookupDto(query, CatalogueReachable: false, Match: null,
                Summary: "Catalogue lookup is switched off on this server.");
        }

        // Keyed by query and by the measurement, because the same recording analysed twice by
        // different executors can produce two different comparisons of the same catalogue data.
        var cacheKey = $"{query}{measured?.TonicName}{measured?.PrimaryMode}{measured?.TempoBpm:0.##}";
        if (_cache.TryGetValue(cacheKey, out var cached)
            && time.GetUtcNow() - cached.At < CacheLifetime)
        {
            return cached.Result;
        }

        ReferenceLookupDto result;
        try
        {
            result = await LookupUncachedAsync(query, measured, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            logger.LogInformation(ex, "Catalogue lookup for '{Query}' did not complete.", query);
            return new ReferenceLookupDto(query, CatalogueReachable: false, Match: null,
                Summary: "The music catalogue could not be reached, so there is nothing to compare against.");
        }

        // Only a completed lookup is cached. Caching an unreachable catalogue would keep the panel
        // dark for six hours after a momentary network blip.
        if (result.CatalogueReachable)
        {
            _cache[cacheKey] = (time.GetUtcNow(), result);
        }
        return result;
    }

    private async Task<ReferenceLookupDto> LookupUncachedAsync(
        string query, ModalResult? measured, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(nameof(MusicBrainzCatalog));
        client.Timeout = RequestTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        var searchUrl = $"{MusicBrainzEndpoint}/recording"
            + $"?query={Uri.EscapeDataString(query)}&fmt=json&limit={SearchLimit}";
        var search = await GetJsonAsync(client, searchUrl, ct);
        if (search is null)
        {
            return new ReferenceLookupDto(query, CatalogueReachable: false, Match: null,
                Summary: "The music catalogue could not be reached, so there is nothing to compare against.");
        }

        if (BestRecording(search.Value) is not { } recording)
        {
            return new ReferenceLookupDto(query, CatalogueReachable: true, Match: null,
                Summary: $"No recording in the catalogue matches “{query}” confidently enough "
                    + "to compare against.");
        }

        // The frozen half of the pair. A miss here is ordinary — most recordings were never
        // submitted — so it costs the identity half of the answer nothing.
        var (referenceKey, referenceScale, referenceBpm) =
            await AcousticBrainzAsync(client, recording.Id, ct);

        var (keyAgreement, tempoAgreement, sentence) = measured is null
            ? (ReferenceAgreement.Unknown, ReferenceAgreement.Unknown,
                "This job has no finished analysis yet, so there is nothing to compare the catalogue against.")
            : ReferenceComparison.Compare(
                measured.TonicName, measured.PrimaryMode, measured.TempoBpm,
                referenceKey, referenceScale, referenceBpm);

        var match = new ReferenceMatchDto(
            Query: query,
            Title: recording.Title,
            Artist: recording.Artist,
            ReleaseTitle: recording.ReleaseTitle,
            RecordingId: recording.Id,
            // Emitted unverified. Checking it would cost a third network round trip on every lookup
            // to learn something the client discovers for free: an <img> that fails to load is
            // hidden, and a missing cover is not worth a request.
            CoverArtUrl: recording.ReleaseId is { } releaseId
                ? $"{CoverArtEndpoint}/{releaseId}/front-250"
                : null,
            LengthSec: recording.LengthMs is { } lengthMs ? (int)Math.Round(lengthMs / 1000.0) : null,
            MatchConfidence: recording.Score / 100.0,
            ReferenceKey: referenceKey,
            ReferenceBpm: referenceBpm,
            KeyAgreement: keyAgreement,
            TempoAgreement: tempoAgreement,
            Comparison: sentence);

        return new ReferenceLookupDto(query, CatalogueReachable: true, Match: match, Summary: sentence);
    }

    /// <summary>
    /// The community's key and tempo for one recording, or nulls when nobody submitted an analysis.
    /// Failures here are swallowed on purpose: the identity half of the answer is still useful.
    /// </summary>
    private async Task<(string? Key, string? Scale, double? Bpm)> AcousticBrainzAsync(
        HttpClient client, string recordingId, CancellationToken ct)
    {
        try
        {
            var document = await GetJsonAsync(client, $"{AcousticBrainzEndpoint}/{recordingId}/low-level", ct);
            if (document is not { } root)
            {
                return (null, null, null);
            }

            string? key = null;
            string? scale = null;
            if (root.TryGetProperty("tonal", out var tonal))
            {
                key = String(tonal, "key_key");
                scale = String(tonal, "key_scale");
            }

            double? bpm = null;
            if (root.TryGetProperty("rhythm", out var rhythm)
                && rhythm.TryGetProperty("bpm", out var bpmValue)
                && bpmValue.ValueKind == JsonValueKind.Number)
            {
                bpm = bpmValue.GetDouble();
            }

            return (key, scale, bpm);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            logger.LogDebug(ex, "No AcousticBrainz analysis for recording {RecordingId}.", recordingId);
            return (null, null, null);
        }
    }

    private sealed record Recording(
        string Id, string Title, string? Artist, string? ReleaseTitle, string? ReleaseId,
        int? LengthMs, int Score);

    /// <summary>The highest-scoring search hit that clears <see cref="MinMatchScore"/>.</summary>
    private static Recording? BestRecording(JsonElement search)
    {
        if (!search.TryGetProperty("recordings", out var recordings)
            || recordings.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Recording? best = null;
        foreach (var entry in recordings.EnumerateArray())
        {
            var score = entry.TryGetProperty("score", out var scoreValue)
                && scoreValue.ValueKind == JsonValueKind.Number
                    ? scoreValue.GetInt32()
                    : 0;
            if (score < MinMatchScore || String(entry, "id") is not { } id || String(entry, "title") is not { } title)
            {
                continue;
            }
            if (best is not null && score <= best.Score)
            {
                continue;
            }

            string? artist = null;
            if (entry.TryGetProperty("artist-credit", out var credits)
                && credits.ValueKind == JsonValueKind.Array)
            {
                artist = string.Concat(credits.EnumerateArray()
                    .Select(credit => String(credit, "name") + (String(credit, "joinphrase") ?? "")));
                artist = string.IsNullOrWhiteSpace(artist) ? null : artist;
            }

            string? releaseTitle = null;
            string? releaseId = null;
            if (entry.TryGetProperty("releases", out var releases)
                && releases.ValueKind == JsonValueKind.Array
                && releases.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } release)
            {
                releaseTitle = String(release, "title");
                releaseId = String(release, "id");
            }

            var length = entry.TryGetProperty("length", out var lengthValue)
                && lengthValue.ValueKind == JsonValueKind.Number
                    ? lengthValue.GetInt32()
                    : (int?)null;

            best = new Recording(id, title, artist, releaseTitle, releaseId, length, score);
        }

        return best;
    }

    /// <summary>
    /// One GET, serialized behind the one-request-per-second gate the catalogues ask for. Returns
    /// null for any non-success status — a 404 from AcousticBrainz is the normal answer for a
    /// recording nobody analysed, not a fault.
    /// </summary>
    private async Task<JsonElement?> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var since = time.GetUtcNow() - _lastRequest;
            if (since < MinRequestInterval)
            {
                await Task.Delay(MinRequestInterval - since, time, ct);
            }
            _lastRequest = time.GetUtcNow();

            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            var payload = await response.Content.ReadAsStringAsync(ct);
            // Parsed into a clone because the JsonDocument is disposed with this scope and an
            // element borrowed from it would be reading freed memory by the time it is used.
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
}
