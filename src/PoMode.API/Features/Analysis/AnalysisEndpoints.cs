using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Visualization;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

public static class AnalysisEndpoints
{
    public static IEndpointRouteBuilder MapAnalysis(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analysis");

        group.MapPost("", async Task<Results<Ok<JobStatusDto>, BadRequest<string>>> (
            HttpRequest request, JobStore store, JobQueue queue, ExecutionPlanner planner, CancellationToken ct) =>
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
            var state = await store.CreateAsync(file.FileName, fresh, ct);
            // Plan synchronously so the response DTO already reflects the execution plan — the
            // background pipeline runs on a separate JobState instance loaded from disk, so it
            // can never retroactively populate the DTO already handed back to the caller.
            try
            {
                state.Plan = await planner.PlanAsync(ct);
            }
            catch (InvalidOperationException ex)
            {
                // No executor is available for some stage: report a Failed job (the pipeline's
                // own graceful-failure shape) instead of letting this surface as a 500 upload.
                state.Stage = JobStage.Failed;
                state.Error = ex.Message;
                await store.SaveAsync(state, ct);
                return TypedResults.Ok(state.ToDto());
            }
            await store.SaveAsync(state, ct);
            await queue.EnqueueAsync(state.JobId, ct);
            return TypedResults.Ok(state.ToDto());
        }).DisableAntiforgery();

        group.MapGet("/{jobId}", async Task<Results<Ok<JobStatusDto>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            if (!IsValidJobId(jobId))
            {
                return TypedResults.NotFound();
            }
            var state = await store.LoadAsync(jobId, ct);
            return state is null ? TypedResults.NotFound() : TypedResults.Ok(state.ToDto());
        });

        group.MapDelete("/{jobId}", async Task<Results<Ok, NotFound>> (
            string jobId, JobStore store, JobCancellationRegistry cancellations, CancellationToken ct) =>
        {
            if (!IsValidJobId(jobId))
            {
                return TypedResults.NotFound();
            }
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
        });

        MapArtifact(group, "notes", "notes.json");
        MapArtifact(group, "chords", "chords.json");
        MapArtifact(group, "result", "result.json");

        // The canvas payload is derived, not stored: one request instead of three, and every colouring
        // decision stays server-side (see the Phase 6 plan's Task 1 ruling). Reads go through
        // ReadArtifact*Async, which holds the per-job lock, so this never streams a half-written file.
        group.MapGet("/{jobId}/visual", async Task<Results<Ok<VisualizationPayload>, NotFound>> (
            string jobId, JobStore store, CancellationToken ct) =>
        {
            if (!IsValidJobId(jobId))
            {
                return TypedResults.NotFound();
            }
            var result = await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct);
            if (result is null)
            {
                return TypedResults.NotFound();
            }
            var notes = await store.ReadArtifactListAsync<NoteEvent>(jobId, "notes.json", ct);
            var chords = await store.ReadArtifactListAsync<ChordSpan>(jobId, "chords.json", ct);
            return TypedResults.Ok(VisualizationBuilder.Build(notes, chords, result));
        });

        // Stem audio for the Web Audio mixer (spec §7). The caller's {name} selects from a fixed
        // allow-list and never becomes part of a path, so there is no traversal surface here at all.
        group.MapGet("/{jobId}/stems/{name}", async Task<Results<FileContentHttpResult, NotFound>> (
            string jobId, string name, JobStore store, CancellationToken ct) =>
        {
            if (!IsValidJobId(jobId))
            {
                return TypedResults.NotFound();
            }

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
            if (!IsValidJobId(jobId))
            {
                return TypedResults.NotFound();
            }
            var bytes = await store.ReadArtifactBytesAsync(jobId, fileName, ct);
            return bytes is null
                ? TypedResults.NotFound()
                : TypedResults.File(bytes, "application/json");
        });

    private static bool IsValidJobId(string jobId)
        => jobId.Length == 32 && jobId.All(char.IsAsciiHexDigitLower);
}
