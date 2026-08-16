using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.MidiExport;

public static class MidiExportEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapMidiExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analysis/{jobId}/midi", async Task<Results<FileContentHttpResult, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            if (jobId.Length != 32 || !jobId.All(char.IsAsciiHexDigitLower))
            {
                return TypedResults.NotFound();
            }

            var jobDir = store.JobDir(jobId);
            var resultPath = Path.Combine(jobDir, "result.json");
            if (!File.Exists(resultPath))
            {
                return TypedResults.NotFound();
            }

            var result = JsonSerializer.Deserialize<ModalResult>(await File.ReadAllTextAsync(resultPath, ct), Json);
            if (result is null)
            {
                return TypedResults.NotFound();
            }

            var notes = await ReadAsync<NoteEvent>(jobDir, "notes.json", ct);
            var chords = await ReadAsync<ChordSpan>(jobDir, "chords.json", ct);

            return TypedResults.File(
                MidiFileBuilder.Build(notes, chords, result),
                contentType: "audio/midi",
                fileDownloadName: $"pomode-{jobId}.mid");
        });

        return app;
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
