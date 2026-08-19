using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.Visualization;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Song statistics and their written interpretation. Both are derived on demand from the stored
/// artifacts and never persisted — the same ruling as <c>/visual</c> and the chord chart: one
/// request instead of four, and every musical decision stays server-side.
/// </summary>
public static class SongStatsEndpoints
{
    public static IEndpointRouteBuilder MapSongStats(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analysis");

        group.MapGet("/{jobId}/stats", async Task<Results<Ok<SongStats>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var stats = await BuildAsync(jobId, store, ct);
            return stats is null ? TypedResults.NotFound() : TypedResults.Ok(stats);
        }).AddEndpointFilter<JobIdEndpointFilter>();

        // The interpreter list is job-independent: it describes what this server can do, not what
        // this song is, so the picker can render before any job finishes.
        group.MapGet("/interpreters", async Task<Ok<List<InterpreterOptionDto>>> (
            SongInterpreterSelector selector, CancellationToken ct) =>
            TypedResults.Ok(await selector.ListAsync(ct)));

        // GET, not POST: the same job and the same interpreter is the same question, and a browser
        // reload should not be a second billed call. Cloud interpreters are still opt-in — the
        // selector only reaches one when ?interpreter= names it.
        group.MapGet("/{jobId}/interpretation", async Task<Results<Ok<SongInterpretationDto>, NotFound>> (
            string jobId,
            string? interpreter,
            JobStore store,
            SongInterpreterSelector selector,
            CancellationToken ct) =>
        {
            var stats = await BuildAsync(jobId, store, ct);
            return stats is null
                ? TypedResults.NotFound()
                : TypedResults.Ok(await selector.InterpretAsync(stats, interpreter, ct));
        }).AddEndpointFilter<JobIdEndpointFilter>();

        return app;
    }

    /// <summary>
    /// Assembles the statistics from the four artifacts, or null when the job has no result yet.
    /// The beat grid is optional by design: it only ever adds the rhythm and harmonic-rhythm figures,
    /// and both degrade to "not available" rather than to a wrong number.
    /// </summary>
    private static async Task<SongStats?> BuildAsync(string jobId, JobStore store, CancellationToken ct)
    {
        var result = await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct);
        if (result is null)
        {
            return null;
        }

        var notes = await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", ct);
        var chords = await store.ReadArtifactListAsync<ChordSpan>(jobId, "chords.json", ct);
        var beats = await store.ReadArtifactAsync<BeatGridDto>(jobId, "beats.json", ct);

        var visual = VisualizationBuilder.Build(notes, chords, result);
        return SongStatsBuilder.Build(visual, chords, result, beats);
    }
}
