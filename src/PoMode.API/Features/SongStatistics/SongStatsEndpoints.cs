using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.API.Platform;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Song statistics and their written interpretation, derived on demand from the stored
/// artifacts — the same ruling as <c>/visual</c>: one request instead of four, and every musical
/// decision stays server-side. Only the written summary is kept, because it costs a model run.
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

        // GET, not POST: the same job and the same interpreter is the same question, and the
        // selector caches the summary per job, so a reload is a file read rather than a model run.
        // Still rate-limited: the first request for each song and interpreter does run a model.
        group.MapGet("/{jobId}/interpretation",
            async Task<Results<ServerSentEventsResult<InterpretationEvent>, Ok<SongInterpretationDto>, NotFound>> (
                string jobId,
                string? interpreter,
                HttpContext http,
                JobStore store,
                SongInterpreterSelector selector,
                CancellationToken ct) =>
        {
            var stats = await BuildAsync(jobId, store, ct);
            if (stats is null)
            {
                return TypedResults.NotFound();
            }

            var events = selector.StreamAsync(jobId, stats, null, null, interpreter, ct);
            return WantsStream(http)
                ? TypedResults.ServerSentEvents(events)
                : TypedResults.Ok((await LastAsync(events)).Summary!);
        })
        .AddEndpointFilter<JobIdEndpointFilter>()
        .RequireRateLimiting(PoRateLimits.InterpretPolicy);

        // POST, unlike the summary above, for three reasons: the question is the request rather than
        // an address, the conversation so far rides with it, and asking the same question twice is a
        // deliberate act rather than a browser reload to be served from cache.
        group.MapPost("/{jobId}/interpretation/ask",
            async Task<Results<ServerSentEventsResult<InterpretationEvent>, Ok<SongAnswerDto>, NotFound, BadRequest<string>>> (
                string jobId,
                SongQuestionRequest request,
                HttpContext http,
                JobStore store,
                SongInterpreterSelector selector,
                CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Question))
            {
                return TypedResults.BadRequest("Ask a question first.");
            }

            var stats = await BuildAsync(jobId, store, ct);
            if (stats is null)
            {
                return TypedResults.NotFound();
            }

            var events = selector.StreamAsync(
                jobId, stats, request.Question.Trim(), request.History, request.Interpreter, ct);
            return WantsStream(http)
                ? TypedResults.ServerSentEvents(events)
                : TypedResults.Ok((await LastAsync(events)).Answer!);
        })
        .AddEndpointFilter<JobIdEndpointFilter>()
        .RequireRateLimiting(PoRateLimits.InterpretPolicy)
        .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// One route per operation, two representations: a page that shows the words as they are written
    /// asks for <c>text/event-stream</c>; anything else gets the finished result as JSON, from the
    /// same events.
    /// </summary>
    private static bool WantsStream(HttpContext http)
        => http.Request.Headers.Accept.ToString().Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

    private static async Task<InterpretationEvent> LastAsync(IAsyncEnumerable<InterpretationEvent> events)
    {
        InterpretationEvent? last = null;
        await foreach (var item in events)
        {
            last = item;
        }
        return last!;
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
        // Absent on jobs analysed before the tempo map existed; the client shows "unknown" for those
        // rather than back-filling a steady tempo that was never measured.
        var tempoMap = await store.ReadArtifactAsync<TempoMapDto>(jobId, "tempo-map.json", ct);

        var visual = VisualizationBuilder.Build(notes, chords, result);
        return SongStatsBuilder.Build(visual, chords, result, beats, tempoMap);
    }
}
