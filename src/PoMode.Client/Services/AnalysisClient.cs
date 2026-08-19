using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;
using PoMode.Shared.Session;

namespace PoMode.Client.Services;

public sealed class AnalysisClient(HttpClient http)
{
    public Task<JobStatusDto?> GetStatusAsync(string jobId)
        => http.GetFromJsonAsync<JobStatusDto>($"api/analysis/{jobId}");

    /// <summary>Who the server thinks we are — feeds the header's session slot.</summary>
    public Task<SessionInfo?> GetSessionAsync()
        => http.GetFromJsonAsync<SessionInfo>("api/session");

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

    public Task<DiagnosticsReport?> GetDiagnosticsAsync()
        => http.GetFromJsonAsync<DiagnosticsReport>("diag");

    /// <summary>The selectable executors per pipeline stage, in the planner's own order.</summary>
    public Task<List<StageExecutorsDto>?> GetExecutorsAsync()
        => http.GetFromJsonAsync<List<StageExecutorsDto>>("api/analysis/executors");

    public Task<BatchStatusDto?> GetBatchAsync(string batchId)
        => http.GetFromJsonAsync<BatchStatusDto>($"api/analysis/batch/{batchId}");

    /// <summary>Every persisted job, newest first, with headline analysis for completed ones.</summary>
    public Task<List<LibraryEntryDto>?> GetLibraryAsync()
        => http.GetFromJsonAsync<List<LibraryEntryDto>>("api/library");

    /// <summary>Runs the modal engine over live mic notes. Null when the server rejected them.</summary>
    public async Task<LiveAnalysisDto?> AnalyzeLiveAsync(IReadOnlyList<NoteEvent> notes)
    {
        var response = await http.PostAsJsonAsync("api/live/analyze", new LiveAnalyzeRequest(notes));
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<LiveAnalysisDto>()
            : null;
    }

    /// <summary>Uploads in-memory audio (the microphone recording) exactly like a file upload.</summary>
    public async Task<JobStatusDto?> UploadAsync(byte[] audioBytes, string fileName, bool clientCanInfer)
    {
        using var content = new MultipartFormDataContent();
        var filePart = new ByteArrayContent(audioBytes);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(filePart, "file", fileName);
        var response = await http.PostAsync(clientCanInfer ? "api/analysis?clientCanInfer=true" : "api/analysis", content);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<JobStatusDto>()
            : null;
    }

    /// <summary>Asks the server to fetch audio from a page URL (yt-dlp). Returns the job, or the
    /// server's reason when it refused.</summary>
    public async Task<(JobStatusDto? Status, string? Error)> AnalyzeFromUrlAsync(string url)
    {
        var response = await http.PostAsJsonAsync("api/analysis/from-url", new AnalyzeUrlRequest(url));
        if (response.IsSuccessStatusCode)
        {
            return (await response.Content.ReadFromJsonAsync<JobStatusDto>(), null);
        }
        string? error = null;
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            try
            {
                error = await response.Content.ReadFromJsonAsync<string>();
            }
            catch (JsonException)
            {
                // fall through to the generic message
            }
        }
        return (null, error ?? $"The server answered {(int)response.StatusCode}.");
    }
}
