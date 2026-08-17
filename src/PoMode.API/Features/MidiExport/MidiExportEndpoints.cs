using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.MidiExport;

public static class MidiExportEndpoints
{
    public static IEndpointRouteBuilder MapMidiExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analysis/{jobId}/midi", async Task<Results<FileContentHttpResult, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var result = await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct);
            if (result is null)
            {
                return TypedResults.NotFound();
            }

            var notes = await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", ct);
            var chords = await store.ReadArtifactListAsync<ChordSpan>(jobId, "chords.json", ct);

            return TypedResults.File(
                MidiFileBuilder.Build(notes, chords, result),
                contentType: "audio/midi",
                fileDownloadName: $"pomode-{jobId}.mid");
        }).AddEndpointFilter<JobIdEndpointFilter>();

        return app;
    }
}
