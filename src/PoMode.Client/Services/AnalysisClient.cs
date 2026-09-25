using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Components.WebAssembly.Http;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Serialization;

namespace PoMode.Client.Services;

public sealed class AnalysisClient(HttpClient http)
{
    public Task<JobStatusDto?> GetStatusAsync(string jobId)
        => http.GetFromJsonAsync<JobStatusDto>($"api/analysis/{jobId}");


    /// <summary>Requests cancellation of a running job; progress updates arrive over SignalR.</summary>
    public Task CancelAsync(string jobId)
        => http.DeleteAsync($"api/analysis/{jobId}");

    public Task<ModalResult?> GetResultAsync(string jobId)
        => http.GetFromJsonAsync<ModalResult>($"api/analysis/{jobId}/result");

    /// <summary>The fast first-look estimate, or null while it hasn't been written yet.</summary>
    public async Task<AnalysisPreviewDto?> GetPreviewAsync(string jobId)
    {
        var response = await http.GetAsync($"api/analysis/{jobId}/preview");
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<AnalysisPreviewDto>()
            : null;
    }

    /// <summary>The canvas payload — note roles and labels already decided server-side.</summary>
    public Task<VisualizationPayload?> GetVisualAsync(string jobId)
        => http.GetFromJsonAsync<VisualizationPayload>($"api/analysis/{jobId}/visual");

    /// <summary>The similarity matrix and novelty curve the section ribbon was cut from, or null when
    /// the song did not divide into sections (the server answers 404, as the ribbon is absent then).</summary>
    public async Task<SongStructureDto?> GetStructureAsync(string jobId)
    {
        var response = await http.GetAsync($"api/analysis/{jobId}/structure");
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SongStructureDto>()
            : null;
    }

    /// <summary>The lead singer's voice type and strongest note. The first request for a song measures
    /// its vocal, so it can take a second or two; later ones are cached server-side.</summary>
    public Task<VoiceProfileDto?> GetVoiceProfileAsync(string jobId)
        => http.GetFromJsonAsync<VoiceProfileDto>($"api/analysis/{jobId}/voice");

    /// <summary>Every derived song/melody statistic, plus the fingerprint paragraph.</summary>
    public Task<SongStats?> GetStatsAsync(string jobId)
        => http.GetFromJsonAsync<SongStats>($"api/analysis/{jobId}/stats");


    /// <summary>The interpreters this server can run, with live availability.</summary>
    public Task<List<InterpreterOptionDto>?> GetInterpretersAsync()
        => http.GetFromJsonAsync<List<InterpreterOptionDto>>("api/analysis/interpreters");

    /// <summary>
    /// The written summary of the statistics, streamed as it is written. <paramref name="interpreter"/>
    /// names one explicitly; omitting it takes the server's default. The last event carries the
    /// finished, checked summary.
    /// </summary>
    public IAsyncEnumerable<InterpretationEvent> StreamInterpretationAsync(
        string jobId, string? interpreter, CancellationToken ct = default)
        => StreamAsync(new HttpRequestMessage(HttpMethod.Get, $"api/analysis/{jobId}/interpretation"
            + (string.IsNullOrWhiteSpace(interpreter) ? "" : $"?interpreter={Uri.EscapeDataString(interpreter)}")), ct);

    /// <summary>
    /// Asks a follow-up about the same measurements, streamed like the summary.
    /// <paramref name="history"/> is the conversation so far — the server keeps none, so the client is
    /// what makes it a conversation at all.
    /// </summary>
    public IAsyncEnumerable<InterpretationEvent> StreamAnswerAsync(
        string jobId, string question, IReadOnlyList<InterpretationTurn> history, string? interpreter,
        CancellationToken ct = default)
        => StreamAsync(new HttpRequestMessage(HttpMethod.Post, $"api/analysis/{jobId}/interpretation/ask")
        {
            Content = JsonContent.Create(new SongQuestionRequest(question, history, interpreter),
                PoModeJsonContext.Default.SongQuestionRequest),
        }, ct);

    /// <summary>
    /// Reads server-sent events as they arrive. Response streaming has to be switched on per request
    /// in the browser, or the fetch buffers the whole body and the words arrive all at once.
    /// </summary>
    private async IAsyncEnumerable<InterpretationEvent> StreamAsync(
        HttpRequestMessage request, [EnumeratorCancellation] CancellationToken ct)
    {
        using var _ = request;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.SetBrowserResponseStreamingEnabled(true);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(response.StatusCode switch
            {
                HttpStatusCode.NotFound => "the analysis for this job is no longer available.",
                HttpStatusCode.TooManyRequests => "that is more requests than the server takes in a minute. Wait a moment and try again.",
                var status => $"the server answered {(int)status}.",
            });
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await foreach (var item in SseParser.Create(body).EnumerateAsync(ct))
        {
            if (JsonSerializer.Deserialize(item.Data, PoModeJsonContext.Default.InterpretationEvent) is { } parsed)
            {
                yield return parsed;
            }
        }
    }

    public Task<DiagnosticsReport?> GetDiagnosticsAsync()
        => http.GetFromJsonAsync<DiagnosticsReport>("diag");

    /// <summary>The selectable executors per pipeline stage, in the planner's own order.</summary>
    public Task<List<StageExecutorsDto>?> GetExecutorsAsync()
        => http.GetFromJsonAsync<List<StageExecutorsDto>>("api/analysis/executors");

    /// <summary>Every persisted job, newest first, with headline analysis for completed ones.</summary>
    public Task<List<LibraryEntryDto>?> GetLibraryAsync()
        => http.GetFromJsonAsync<List<LibraryEntryDto>>("api/library");

    /// <summary>
    /// Starts analysing a finished resumable upload (js/upload/resumable-upload.js), with this
    /// browser's Tier-2 capability and the per-stage model picks on the query string (keys from the
    /// shared <see cref="StageNames.ExecutorQueryKeys"/> table; Auto sends nothing).
    ///
    /// <para>A dropped connection is retried: the server answers a repeat for the same upload with the
    /// job the first call started, so a response lost on a flaky network does not strand the file.
    /// Returns the server's reason when it refuses.</para>
    /// </summary>
    public async Task<(JobStatusDto? Status, string? Error)> StartFromUploadAsync(
        string uploadId, bool clientCanInfer, IReadOnlyDictionary<string, string> executorChoices)
    {
        var query = new List<string>(4);
        if (clientCanInfer)
        {
            query.Add("clientCanInfer=true");
        }
        foreach (var (stage, queryKey) in StageNames.ExecutorQueryKeys)
        {
            if (executorChoices.TryGetValue(stage, out var name))
            {
                query.Add($"{queryKey}={Uri.EscapeDataString(name)}");
            }
        }
        var url = $"api/analysis/uploads/{Uri.EscapeDataString(uploadId)}"
            + (query.Count == 0 ? "" : "?" + string.Join("&", query));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.PostAsync(url, content: null);
                if (response.IsSuccessStatusCode)
                {
                    return (await response.Content.ReadFromJsonAsync<JobStatusDto>(), null);
                }
                return (null, await ReasonAsync(response));
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
    }

    /// <summary>The server's refusals are JSON strings written for people; anything else gets a
    /// plain sentence rather than a status code.</summary>
    private static async Task<string> ReasonAsync(HttpResponseMessage response)
    {
        try
        {
            if (await response.Content.ReadFromJsonAsync<string>() is { Length: > 0 } reason)
            {
                return reason;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // An empty or non-JSON body: fall through to the generic sentence.
        }
        return response.StatusCode == HttpStatusCode.NotFound
            ? "The upload expired before the analysis could start. Please upload the file again."
            : "The analyzer would not accept the file.";
    }

    /// <summary>Standard chord progression presets across genres and modes.</summary>
    public Task<List<ChordProgressionDefinition>?> GetModalProgressionsAsync()
        => http.GetFromJsonAsync<List<ChordProgressionDefinition>>("api/modal-melodies/progressions");

    /// <summary>Generates an algorithmic melody and chord accompaniment for a scale mode and progression.</summary>
    public async Task<GeneratedMelodyDto?> GenerateModalMelodyAsync(ModalMelodyRequest request)
    {
        var response = await http.PostAsJsonAsync("api/modal-melodies/generate", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<GeneratedMelodyDto>()
            : null;
    }


    /// <summary>
    /// Uploads a hum take together with the backing it was sung over. The backing rides along as the
    /// same query parameters the WAV and MIDI exports take, so the server can regenerate the exact
    /// progression the user heard and put it under their melody — a solo hum contains no harmony for
    /// a chord recognizer to find, and asking one to look would invent a chord track nobody sang.
    /// </summary>
    public async Task<JobStatusDto?> UploadHumTakeAsync(byte[] wavBytes, ModalMelodyRequest backing)
    {
        using var content = new MultipartFormDataContent();
        var filePart = new ByteArrayContent(wavBytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(filePart, "file", "hum.wav");

        var url = "api/modal-melodies/hum"
            + $"?tonicPitchClass={backing.TonicPitchClass}"
            + $"&mode={backing.Mode}"
            + $"&progressionId={Uri.EscapeDataString(backing.ProgressionId)}"
            + $"&bpm={backing.Bpm.ToString(CultureInfo.InvariantCulture)}"
            + $"&style={backing.Style}"
            + $"&seed={backing.Seed}"
            + $"&targetPurity={backing.TargetPurity.ToString(CultureInfo.InvariantCulture)}";

        var response = await http.PostAsync(url, content);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JobStatusDto>()
            : null;
    }

    /// <summary>The caller's earlier takes over this backing's mode and progression. Key and tempo are
    /// not sent: the server matches takes across them on purpose.</summary>
    public Task<TakeHistoryDto?> GetTakeHistoryAsync(ModalMelodyRequest backing)
        => http.GetFromJsonAsync<TakeHistoryDto>(
            "api/modal-melodies/takes"
            + $"?mode={backing.Mode}"
            + $"&progressionId={Uri.EscapeDataString(backing.ProgressionId)}"
            + $"&targetPurity={backing.TargetPurity.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>The caller's sung range, and the key that fits <paramref name="mode"/> to it once
    /// there are enough takes to say.</summary>
    public Task<VocalRangeDto?> GetVocalRangeAsync(ScaleMode mode)
        => http.GetFromJsonAsync<VocalRangeDto>($"api/modal-melodies/voice?mode={mode}");

    /// <summary>Synthesizes the melody and chords into WAV audio and queues an end-to-end analysis job in the Song Analyzer.</summary>
    public async Task<JobStatusDto?> AnalyzeModalMelodyAsync(ModalMelodyRequest request)
    {
        var response = await http.PostAsJsonAsync("api/modal-melodies/analyze", request);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JobStatusDto>()
            : null;
    }
}
