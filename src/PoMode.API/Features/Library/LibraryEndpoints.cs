using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Library;

public static class LibraryEndpoints
{
    public static IEndpointRouteBuilder MapLibrary(this IEndpointRouteBuilder app)
    {
        // Requires auth: listing enumerates every job id, and unguessable ids are the only gate
        // on the anonymous per-job read endpoints — an open listing would defeat that.
        app.MapGet("/api/library", async Task<Ok<List<LibraryEntryDto>>> (
            JobStore store, CancellationToken ct) =>
        {
            var entries = new List<LibraryEntryDto>();
            foreach (var jobId in store.ListJobIds())
            {
                if (!JobId.IsValid(jobId))
                {
                    continue; // a stray folder (temp dir, manual copy) is not a job
                }
                var state = await store.LoadAsync(jobId, ct);
                if (state is null)
                {
                    continue;
                }

                // The pipeline stamps headline facts on completion; only jobs persisted before
                // that field existed pay the full result.json read as a fallback.
                var tonicName = state.TonicName;
                var primaryMode = state.PrimaryMode;
                var tempoBpm = state.TempoBpm;
                if (tonicName is null && state.Stage == JobStage.Complete
                    && await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct) is { } result)
                {
                    tonicName = result.TonicName;
                    primaryMode = result.PrimaryMode?.ToString();
                    tempoBpm = result.TempoBpm;
                }
                entries.Add(new LibraryEntryDto(
                    state.JobId,
                    state.InputFileName,
                    state.CreatedAt,
                    state.Stage,
                    tonicName,
                    primaryMode,
                    tempoBpm));
            }
            return TypedResults.Ok(entries.OrderByDescending(e => e.CreatedAt).ToList());
        }).RequireAuthorization();

        return app;
    }
}
