using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.API.Platform;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Reference;

/// <summary>
/// What a public music catalogue says about this job's recording, next to what PoMode measured.
///
/// <para>Derived on demand and never persisted, like <c>/stats</c> and <c>/visual</c>: the answer
/// belongs to the catalogue, not to this app, and a stored copy would be the one part of a job that
/// could silently go out of date.</para>
/// </summary>
public static class ReferenceEndpoints
{
    public static IEndpointRouteBuilder MapReference(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/analysis").WithTags("Reference");

        group.MapGet("/{jobId}/reference", async Task<Results<Ok<ReferenceLookupDto>, NotFound>> (
            string jobId,
            string? query,
            JobStore store,
            MusicBrainzCatalog catalogue,
            CancellationToken ct) =>
        {
            var state = await store.LoadAsync(jobId, ct);
            if (state is null)
            {
                return TypedResults.NotFound();
            }

            // An explicit ?query= is the user correcting the guess, which is the only repair available
            // for a file named "track03.mp3" — so it is honoured as given, not re-cleaned.
            var search = string.IsNullOrWhiteSpace(query)
                ? ReferenceQuery.FromFileName(state.InputFileName)
                : query.Trim();

            if (search is null)
            {
                // Either PoMode generated this audio itself or the name carried nothing searchable.
                // Both are facts about the file, so the catalogue is never asked.
                return TypedResults.Ok(new ReferenceLookupDto(
                    Query: "",
                    CatalogueReachable: false,
                    Match: null,
                    Summary: ReferenceQuery.IsAppGenerated(state.InputFileName)
                        ? "This recording was made in PoMode, so there is no catalogue release to compare it to."
                        : "There is nothing searchable in this file's name. Type a title and artist to look it up."));
            }

            // Null until the pipeline finishes: the identity half of the answer is still worth
            // showing while a job runs, and the comparison half says so rather than inventing one.
            var measured = await store.ReadArtifactAsync<ModalResult>(jobId, "result.json", ct);
            return TypedResults.Ok(await catalogue.LookupAsync(search, measured, ct));
        })
        .AddEndpointFilter<JobIdEndpointFilter>()
        // Every call here can reach two third-party catalogues, both of which ask callers to be
        // modest. The catalogue client paces its own requests; this stops one browser from queueing
        // up minutes of paced requests in the first place.
        .RequireRateLimiting(PoRateLimits.CataloguePolicy)
        .WithName("GetReferenceMatch")
        .WithSummary("Compares this job's measured key and tempo against MusicBrainz and AcousticBrainz.");

        return app;
    }
}
