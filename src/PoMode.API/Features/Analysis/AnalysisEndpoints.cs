using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Audio;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Features.Visualization;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysis(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analysis");
        group.AddEndpointFilter<JobIdEndpointFilter>();

        // Stays anonymous: RadzenUpload posts the file itself from the browser and cannot attach
        // the FakeAuth headers the C# HttpClient carries. Every other write endpoint requires auth.
        group.MapPost("", async Task<Results<Ok<JobStatusDto>, BadRequest<string>>> (
            HttpRequest request, AnalysisIntake intake, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return TypedResults.BadRequest("Expected a multipart form upload.");
            }

            IFormCollection form;
            try
            {
                form = await request.ReadFormAsync(ct);
            }
            catch (InvalidDataException)
            {
                // An empty/malformed multipart body (e.g. no parts at all) fails ASP.NET Core's
                // multipart parser before any file can be inspected — treat it the same as "no file".
                return TypedResults.BadRequest("No file uploaded.");
            }

            var file = form.Files.FirstOrDefault();
            if (file is null)
            {
                return TypedResults.BadRequest("No file uploaded.");
            }
            if (file.Length > AudioFormatValidator.MaxBytes)
            {
                return TypedResults.BadRequest("File exceeds the 100 MB limit.");
            }

            await using var stream = file.OpenReadStream();
            var header = new byte[12];
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
            if (!AudioFormatValidator.IsSupported(header.AsSpan(0, read), out _))
            {
                return TypedResults.BadRequest("Only .mp3 and .wav files are supported.");
            }

            await using var fresh = file.OpenReadStream();
            // Tier 2 availability is a per-job property of the uploading browser (spec §4): the
            // client probes for onnxruntime-web support and declares it here. Absent or false, the
            // browser tier is simply invisible and planning behaves exactly as before.
            var clientCanInfer = bool.TryParse(request.Query["clientCanInfer"], out var canInfer) && canInfer;
            var state = await intake.StartAsync(file.FileName, fresh, clientCanInfer, ct);
            return TypedResults.Ok(state.ToDto());
        }).DisableAntiforgery();

        group.MapGet("/{jobId}", async Task<Results<Ok<JobStatusDto>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            var state = await store.LoadAsync(jobId, ct);
            return state is null ? TypedResults.NotFound() : TypedResults.Ok(state.ToDto());
        });

        group.MapDelete("/{jobId}", async Task<Results<Ok, NotFound>> (
            string jobId, JobStore store, JobCancellationRegistry cancellations, CancellationToken ct) =>
        {
            var state = await store.LoadAsync(jobId, ct);
            if (state is null)
            {
                return TypedResults.NotFound();
            }
            if (!cancellations.TryCancel(jobId)
                && state.Stage is not (JobStage.Complete or JobStage.Failed or JobStage.Cancelled))
            {
                state.Stage = JobStage.Cancelled;
                await store.SaveAsync(state, ct);
            }
            return TypedResults.Ok();
        }).RequireAuthorization();

        MapArtifact(group, "notes", "notes.json");
        MapArtifact(group, "chords", "chords.json");
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
            return TypedResults.Ok(VisualizationBuilder.Build(notes, chords, result));
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
            // missing directory must degrade to "duration unknown" rather than throwing a 500. The
            // validator then bounds times by the upload path's own ceiling instead.
            var jobDir = store.JobDir(jobId);
            var inputPath = Directory.Exists(jobDir)
                ? Directory.EnumerateFiles(jobDir, "input.*").FirstOrDefault()
                : null;
            var duration = inputPath is null ? null : AudioDecoder.TryReadDurationSeconds(inputPath);

            if (ClientResultValidator.Validate(notes, duration) is { } problem)
            {
                return TypedResults.BadRequest(problem);
            }
            // Racing another post: whoever completes the waiter first wins, the loser gets a 404.
            return registry.TryComplete(jobId, notes)
                ? TypedResults.Ok()
                : TypedResults.NotFound();
        }).RequireAuthorization();

        // Stem audio for the Web Audio mixer (spec §7). The caller's {name} selects from a fixed
        // allow-list and never becomes part of a path, so there is no traversal surface here at all.
        group.MapGet("/{jobId}/stems/{name}", async Task<Results<FileContentHttpResult, NotFound>> (
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

            var bytes = await store.ReadArtifactBytesAsync(jobId, fileName, ct);
            return bytes is null ? TypedResults.NotFound() : TypedResults.File(bytes, contentType);
        });

        return app;
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
