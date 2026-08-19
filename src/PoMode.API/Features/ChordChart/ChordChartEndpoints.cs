using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordChart;

public static class ChordChartEndpoints
{
    public static IEndpointRouteBuilder MapChordChart(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/analysis/{jobId}/chord-chart", async Task<Results<FileContentHttpResult, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var result = await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct);
            if (result is null)
            {
                return TypedResults.NotFound();
            }

            var state = await store.LoadAsync(jobId, ct);
            var chords = await store.ReadArtifactListAsync<ChordSpan>(jobId, "chords.json", ct);

            return TypedResults.File(
                Encoding.UTF8.GetBytes(ChordChartBuilder.Build(chords, result, state?.InputFileName ?? jobId)),
                contentType: "text/plain; charset=utf-8",
                fileDownloadName: $"pomode-{jobId}-chords.txt");
        }).AddEndpointFilter<JobIdEndpointFilter>();

        return app;
    }
}
