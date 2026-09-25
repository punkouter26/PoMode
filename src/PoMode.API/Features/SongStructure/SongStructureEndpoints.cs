using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStructure;

public static class SongStructureEndpoints
{
    /// <summary>
    /// Past this many bars the matrix stops being a picture anyone reads cell by cell, and its bytes
    /// grow with the square. The ribbon still works on any length; only the drawing is withheld.
    /// </summary>
    private const int MaxDrawnBars = 256;

    public static IEndpointRouteBuilder MapSongStructure(this IEndpointRouteBuilder app)
    {
        // Derived per request from the stored artifacts, same ruling as /visual, whose section ribbon
        // this explains. A song that did not divide has nothing to explain and answers 404, like the
        // ribbon, which is simply absent then.
        app.MapGet("/api/analysis/{jobId}/structure", async Task<Results<Ok<SongStructureDto>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            if (!JobId.IsValid(jobId)
                || await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct) is not { } result)
            {
                return TypedResults.NotFound();
            }
            var chords = await store.ReadArtifactListAsync<ChordSpan>(jobId, "chords.json", ct);
            var tempoMap = await store.ReadArtifactAsync<TempoMapDto>(jobId, "tempo-map.json", ct);
            var beats = await store.ReadArtifactAsync<BeatGridDto>(jobId, "beats.json", ct);

            var analysis = SongSectionBuilder.Analyse(chords, result, tempoMap, beats);
            return analysis is null || analysis.Sections.Count == 0 || analysis.Novelty.Length > MaxDrawnBars
                ? TypedResults.NotFound()
                : TypedResults.Ok(ToDto(analysis));
        })
        .WithTags("Analysis")
        .WithName("GetSongStructure")
        .WithSummary("The bar-by-bar harmonic similarity matrix and novelty curve the song's sections were cut from.");

        return app;
    }

    public static SongStructureDto ToDto(SongSectionBuilder.Analysis analysis)
    {
        var count = analysis.Novelty.Length;
        var cells = new byte[count * count];
        for (var row = 0; row < count; row++)
        {
            for (var column = 0; column < count; column++)
            {
                cells[(row * count) + column] = (byte)Math.Round(Math.Clamp(analysis.Similarity[row, column], 0, 1) * 255);
            }
        }

        var half = SongSectionBuilder.KernelBars / 2;
        var boundaries = analysis.Boundaries.Count;
        var explanation =
            "Each square compares two bars by their chords, more strongly coloured the more harmony they share, so a repeated "
            + "section shows up as a strong block off the diagonal. The checkerboard sliding down the diagonal "
            + $"scores how unlike the {half} bars before each bar line are from the {half} after it; "
            + (boundaries == 1 ? "its tallest peak became the section boundary." : $"its {boundaries} tallest peaks became the section boundaries.");

        return new SongStructureDto(
            analysis.BarEdges,
            Convert.ToBase64String(cells),
            analysis.Novelty,
            analysis.Boundaries,
            SongSectionBuilder.KernelBars,
            analysis.Sections,
            explanation);
    }
}
