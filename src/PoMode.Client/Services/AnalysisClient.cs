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

    public Task<List<NoteEvent>?> GetNotesAsync(string jobId)
        => http.GetFromJsonAsync<List<NoteEvent>>($"api/analysis/{jobId}/notes");

    public Task<List<ChordSpan>?> GetChordsAsync(string jobId)
        => http.GetFromJsonAsync<List<ChordSpan>>($"api/analysis/{jobId}/chords");

    public Task<ModalResult?> GetResultAsync(string jobId)
        => http.GetFromJsonAsync<ModalResult>($"api/analysis/{jobId}/result");

    /// <summary>The canvas payload — note roles and labels already decided server-side.</summary>
    public Task<VisualizationPayload?> GetVisualAsync(string jobId)
        => http.GetFromJsonAsync<VisualizationPayload>($"api/analysis/{jobId}/visual");

    public Task<DiagnosticsReport?> GetDiagnosticsAsync()
        => http.GetFromJsonAsync<DiagnosticsReport>("diag");

    public Task<BatchStatusDto?> GetBatchAsync(string batchId)
        => http.GetFromJsonAsync<BatchStatusDto>($"api/analysis/batch/{batchId}");

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

    /// <summary>Asks the local Ollama copilot to explain one window. Never throws for an absent
    /// copilot — that comes back as a reply with <c>Available = false</c>.</summary>
    public async Task<CopilotReply?> ExplainAsync(string jobId, int windowIndex)
    {
        var response = await http.PostAsJsonAsync("api/copilot/explain", new CopilotRequest(jobId, windowIndex));
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<CopilotReply>()
            : new CopilotReply(false, null, null, "That analysis window is no longer available.");
    }
}
