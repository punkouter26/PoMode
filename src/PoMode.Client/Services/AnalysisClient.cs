using System.Net.Http.Json;
using PoMode.Shared.Analysis;

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
}
