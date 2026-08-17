using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.MusicXml;

public static class MusicXmlEndpoints
{
    public static IEndpointRouteBuilder MapMusicXmlExport(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analysis/{jobId}/musicxml", async Task<Results<FileContentHttpResult, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var result = await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct);
            if (result is null)
            {
                return TypedResults.NotFound();
            }

            var notes = await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", ct);
            var chords = await store.ReadArtifactListAsync<ChordSpan>(jobId, "chords.json", ct);
            var state = await store.LoadAsync(jobId, ct);
            var title = Path.GetFileNameWithoutExtension(state?.InputFileName ?? "PoMode Lead Sheet");

            return TypedResults.File(
                Encoding.UTF8.GetBytes(MusicXmlBuilder.Build(notes, chords, result, title)),
                contentType: "application/vnd.recordare.musicxml+xml",
                fileDownloadName: $"pomode-{jobId}.musicxml");
        }).AddEndpointFilter<JobIdEndpointFilter>();

        return app;
    }
}
