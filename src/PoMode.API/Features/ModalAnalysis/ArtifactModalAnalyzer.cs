using System.Text.Json;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalAnalysis;

/// <summary>Stage 4: reads notes.json + chords.json, runs the deterministic engine, writes result.json.</summary>
public sealed class ArtifactModalAnalyzer : IModalAnalyzer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task AnalyzeAsync(StageContext context, CancellationToken ct)
    {
        var notes = await ReadAsync<NoteEvent>(context.JobDir, "notes.json", ct);
        var chords = await ReadAsync<ChordSpan>(context.JobDir, "chords.json", ct);

        var result = ModalAnalysisEngine.Analyze(notes, chords);

        await File.WriteAllTextAsync(
            Path.Combine(context.JobDir, "result.json"),
            JsonSerializer.Serialize(result, Json),
            ct);
    }

    private static async Task<IReadOnlyList<T>> ReadAsync<T>(string jobDir, string fileName, CancellationToken ct)
    {
        var path = Path.Combine(jobDir, fileName);
        if (!File.Exists(path))
        {
            return [];
        }
        return JsonSerializer.Deserialize<List<T>>(await File.ReadAllTextAsync(path, ct), Json) ?? [];
    }
}
