using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Audio;
using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.Auth;
using PoMode.API.Features.Uploads;
using PoMode.API.Pipeline;
using PoMode.API.Platform;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysis(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analysis");
        group.AddEndpointFilter<JobIdEndpointFilter>();

        // The one way to start analysing a file: it arrives over tus at /api/uploads (resumable, see
        // ResumableUploads) and this call hands the finished upload to the pipeline. It used to be a
        // multipart POST to this group's root, which restarted a 100 MB memo from zero whenever a
        // phone lost signal; that endpoint is gone rather than kept beside this one.
        //
        // Requires a session like every other write: the job has to know whose library it belongs to.
        group.MapPost("/uploads/{uploadId}", async Task<Results<Ok<JobStatusDto>, BadRequest<string>, NotFound, Conflict<string>>> (
            string uploadId, HttpRequest request, ResumableUploads uploads, AnalysisIntake intake, CancellationToken ct) =>
        {
            if (PoUser.IdOf(request.HttpContext.User) is not { } owner)
            {
                return TypedResults.NotFound();
            }
            // Tier 2 availability is a per-job property of the uploading browser (spec §4): the
            // client probes for onnxruntime-web support and declares it here. Absent or false, the
            // browser tier is simply invisible and planning behaves exactly as before.
            var clientCanInfer = bool.TryParse(request.Query["clientCanInfer"], out var canInfer) && canInfer;
            // The home page's per-stage model pickers arrive as plain query params; an absent or
            // bogus name simply leaves that stage on the planner's normal ranked order.
            var handoff = await uploads.StartAnalysisAsync(
                uploadId, owner, intake, clientCanInfer, PreferredExecutors(request), ct);
            // The throw arm makes an unhandled outcome fail closed (500) instead of silently
            // answering 200 for a file that was never queued.
            return handoff switch
            {
                UploadHandoff.Started started => TypedResults.Ok(started.Status),
                UploadHandoff.Missing => TypedResults.NotFound(),
                UploadHandoff.Incomplete incomplete => TypedResults.Conflict(
                    $"The upload has {incomplete.Offset} of {incomplete.Length} bytes; resume it before starting the analysis."),
                UploadHandoff.Rejected { Reason: UploadRejection.TooLarge } => TypedResults.BadRequest("File exceeds the 100 MB limit."),
                UploadHandoff.Rejected { Reason: UploadRejection.UnsupportedFormat } => TypedResults.BadRequest("Only .mp3 and .wav files are supported."),
                _ => throw new InvalidOperationException($"Unhandled upload hand-off {handoff}."),
            };
        })
        .RequireAuthorization()
        // The two guards answer different questions: the rate limit bounds how fast one client may
        // ask, the capacity filter bounds how much this server has already agreed to do. Neither
        // substitutes for the other — see QueueCapacityFilter. A refusal leaves the upload in place,
        // so the client can retry this call without sending the file again.
        .RequireRateLimiting(PoRateLimits.UploadPolicy)
        .AddEndpointFilter<QueueCapacityFilter>();

        // The selectable executors per stage — the planner owns the whole answer (structure,
        // ordering, eligibility, defaults); this endpoint only serializes it.
        group.MapGet("/executors", async (ExecutionPlanner planner, CancellationToken ct)
            => TypedResults.Ok(await planner.ListOptionsAsync(ct)));

        group.MapGet("/{jobId}", async Task<Results<Ok<JobStatusDto>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var state = await store.LoadAsync(jobId, ct);
            return state is null ? TypedResults.NotFound() : TypedResults.Ok(state.ToDto());
        });

        group.MapDelete("/{jobId}", async Task<Results<Ok, NotFound>> (
            string jobId, HttpContext context, JobStore store, JobCancellationRegistry cancellations, CancellationToken ct) =>
        {
            var state = await store.LoadAsync(jobId, ct);
            // Someone else's job answers exactly like a missing one, so the endpoint cannot be used to
            // learn which ids exist. An unowned legacy job stays cancellable by any signed-in caller.
            if (state is null || (state.OwnerId is not null && state.OwnerId != PoUser.IdOf(context.User)))
            {
                return TypedResults.NotFound();
            }
            if (!cancellations.TryCancel(jobId) && !state.Stage.IsTerminal())
            {
                state.Stage = JobStage.Cancelled;
                await store.SaveAsync(state, ct);
            }
            return TypedResults.Ok();
        }).RequireAuthorization();

        MapArtifact(group, "notes", "notes.json");
        MapArtifact(group, "notes-backing", "notes-backing.json");
        MapArtifact(group, "chords", "chords.json");
        MapArtifact(group, "beats", "beats.json");
        MapArtifact(group, "tempo-map", "tempo-map.json");
        MapArtifact(group, "preview", "preview.json");
        MapArtifact(group, "result", "result.json");

        // The canvas payload is derived, not stored: one request instead of three, and every colouring
        // decision stays server-side (see the Phase 6 plan's Task 1 ruling). Reads go through
        // ReadArtifact*Async, which holds the per-job lock, so this never streams a half-written file.
        group.MapGet("/{jobId}/visual", async Task<Results<Ok<VisualizationPayload>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var result = await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct);
            if (result is null)
            {
                return TypedResults.NotFound();
            }
            var notes = await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", ct);
            var chords = await store.ReadArtifactListAsync<ChordSpan>(jobId, "chords.json", ct);
            // Null for a job analysed before the tempo map existed; the canvas then draws no tempo lane.
            var tempoMap = await store.ReadArtifactAsync<TempoMapDto>(jobId, "tempo-map.json", ct);
            // Both only sharpen the section grid; without them sections fall back to fixed bars.
            var beats = await store.ReadArtifactAsync<BeatGridDto>(jobId, "beats.json", ct);
            return TypedResults.Ok(VisualizationBuilder.Build(notes, chords, result, tempoMap, beats));
        });

        // The chord pad is derived, not stored: chords.json → triad NoteEvents so the mixer's
        // "Synth chords" layer plays server-decided pitches (musical decisions stay out of JS,
        // same ruling as /visual).
        group.MapGet("/{jobId}/notes-chords", async Task<Results<Ok<IReadOnlyList<NoteEvent>>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var chords = await store.ReadArtifactListAsync<ChordSpan>(jobId, "chords.json", ct);
            return chords.Count == 0
                ? TypedResults.NotFound()
                : TypedResults.Ok(ChordPadBuilder.Build(chords));
        });

        // Tier 2's return path (spec §4 step 3). The notes come from a browser, so they are untrusted:
        // 404 when nothing is waiting (unknown job, already timed out, or a duplicated post) and 400
        // with a reason when the payload fails validation. Accepting bad notes here would put them in
        // notes.json, the modal engine and the MIDI export.
        group.MapPost("/{jobId}/client-result", Results<Ok, BadRequest<string>, NotFound> (
            string jobId,
            IReadOnlyList<NoteEvent> notes,
            JobStore store,
            ClientWorkRegistry registry) =>
        {
            if (!registry.IsWaiting(jobId))
            {
                return TypedResults.NotFound();
            }

            // The job folder can be gone — purged by the nightly sweep, or deleted mid-flight — so a
            // missing input must degrade to "duration unknown" rather than throwing a 500. The
            // validator then bounds times by the upload path's own ceiling instead.
            var inputPath = store.TryFindInputPath(jobId);
            var duration = inputPath is null ? null : AudioDecoder.TryReadDurationSeconds(inputPath);

            if (ClientResultValidator.Validate(notes, duration) is { } problem)
            {
                return TypedResults.BadRequest(problem);
            }
            // The canvas and mixer virtualize over start-sorted notes; the bundled decoder sorts,
            // but a third-party client is not obliged to — enforce the ordering contract here.
            notes = [.. notes.OrderBy(note => note.StartSec)];
            // Racing another post: whoever completes the waiter first wins, the loser gets a 404.
            return registry.TryComplete(jobId, notes)
                ? TypedResults.Ok()
                : TypedResults.NotFound();
        }).RequireAuthorization();

        // Stem audio for the Web Audio mixer (spec §7). The caller's {name} selects from a fixed
        // allow-list and never becomes part of a path, so there is no traversal surface here at all.
        // Served from disk with range support: stems are ~40MB and written once before the job
        // completes, so buffering them per request would only churn the large-object heap.
        group.MapGet("/{jobId}/stems/{name}", async Task<Results<PhysicalFileHttpResult, NotFound>> (
            string jobId, string name, JobStore store, CancellationToken ct) =>
        {
            string fileName;
            string contentType;
            switch (name)
            {
                case "vocals":
                case "instrumental":
                    fileName = $"{name}.wav";
                    contentType = "audio/wav";
                    break;
                case "mix":
                    // The original upload. Its extension came from the uploaded file name, so it is
                    // derived rather than fixed — but Path.GetExtension can never return a value
                    // containing a directory separator, and Path.GetFileName strips any path part,
                    // so the result is still confined to this job's directory.
                    var state = await store.LoadAsync(jobId, ct);
                    if (state is null)
                    {
                        return TypedResults.NotFound();
                    }
                    fileName = Path.GetFileName(store.InputPath(state));
                    contentType = Path.GetExtension(fileName).ToLowerInvariant() == ".mp3"
                        ? "audio/mpeg"
                        : "audio/wav";
                    break;
                default:
                    return TypedResults.NotFound();
            }

            var path = await store.GetArtifactPathAsync(jobId, fileName, ct);
            return path is null
                ? TypedResults.NotFound()
                : TypedResults.PhysicalFile(path, contentType, enableRangeProcessing: true);
        });

        // A waveform sketch for the progress view, readable while the job is still running: the vocal
        // stem once separation has written it, the upload itself before then. Derived per request
        // like /visual; a streamed min/max pass over one file is cheap next to what the view replaces.
        group.MapGet("/{jobId}/peaks", async Task<Results<Ok<WaveformPeaksDto>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var state = await store.LoadAsync(jobId, ct);
            if (state is null)
            {
                return TypedResults.NotFound();
            }
            var vocals = await store.GetArtifactPathAsync(jobId, "vocals.wav", ct);
            var (path, source) = vocals is not null
                ? (vocals, "vocals")
                : (await store.GetArtifactPathAsync(jobId, Path.GetFileName(store.InputPath(state)), ct), "mix");
            return path is not null && AudioDecoder.TryReadPeaks(path, PeakColumns) is var (peaks, duration)
                ? TypedResults.Ok(new WaveformPeaksDto(duration, source, peaks))
                : TypedResults.NotFound();
        });

        return app;
    }

    /// <summary>Slices in a waveform sketch: finer than a phone is wide, coarse enough to stay a few kB.</summary>
    private const int PeakColumns = 512;

    /// <summary>Reads the per-stage executor picks off the upload query string via the shared
    /// key table; empty when the user left every stage on Auto.</summary>
    private static Dictionary<string, string> PreferredExecutors(HttpRequest request)
    {
        var preferred = new Dictionary<string, string>();
        foreach (var (stage, queryKey) in StageNames.ExecutorQueryKeys)
        {
            var value = request.Query[queryKey].ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                preferred[stage] = value;
            }
        }
        return preferred;
    }

    private static void MapArtifact(RouteGroupBuilder group, string route, string fileName)
        => group.MapGet($"/{{jobId}}/{route}", async Task<Results<FileContentHttpResult, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var bytes = await store.ReadArtifactBytesAsync(jobId, fileName, ct);
            return bytes is null
                ? TypedResults.NotFound()
                : TypedResults.File(bytes, "application/json");
        });
}
