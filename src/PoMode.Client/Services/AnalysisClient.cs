using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;

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

    /// <summary>Every derived song/melody statistic, plus the fingerprint paragraph.</summary>
    public Task<SongStats?> GetStatsAsync(string jobId)
        => http.GetFromJsonAsync<SongStats>($"api/analysis/{jobId}/stats");


    /// <summary>The interpreters this server can run, with live availability.</summary>
    public Task<List<InterpreterOptionDto>?> GetInterpretersAsync()
        => http.GetFromJsonAsync<List<InterpreterOptionDto>>("api/analysis/interpreters");

    /// <summary>
    /// A written interpretation of the statistics. <paramref name="interpreter"/> names one
    /// explicitly — the only way to reach a paid cloud model; omitting it takes the free default.
    /// Returns null when the job has no result yet rather than throwing, so a caller racing the
    /// pipeline degrades to "not ready" instead of an error toast.
    /// </summary>
    public async Task<SongInterpretationDto?> GetInterpretationAsync(string jobId, string? interpreter = null)
    {
        var url = $"api/analysis/{jobId}/interpretation"
            + (string.IsNullOrWhiteSpace(interpreter) ? "" : $"?interpreter={Uri.EscapeDataString(interpreter)}");
        var response = await http.GetAsync(url);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SongInterpretationDto>()
            : null;
    }

    /// <summary>
    /// Asks a follow-up about the same measurements. <paramref name="history"/> is the conversation
    /// so far — the server keeps none, so the client is what makes it a conversation at all.
    ///
    /// <para>Returns null when the server refused (no analysis yet, or the per-minute limit on model
    /// calls was reached), so a caller degrades to a message rather than an exception.</para>
    /// </summary>
    public async Task<SongAnswerDto?> AskAboutSongAsync(
        string jobId, string question, IReadOnlyList<InterpretationTurn> history, string? interpreter = null)
    {
        var response = await http.PostAsJsonAsync(
            $"api/analysis/{jobId}/interpretation/ask",
            new SongQuestionRequest(question, history, interpreter));
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<SongAnswerDto>()
            : null;
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
