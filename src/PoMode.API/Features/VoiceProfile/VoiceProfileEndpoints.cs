using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.VoiceProfile;

public static class VoiceProfileEndpoints
{
    public static IEndpointRouteBuilder MapVoiceProfile(this IEndpointRouteBuilder app)
    {
        // Open to the job id like the other per-job reads. The first call measures the audio and the
        // result is cached with the job, so the cost is paid once per song, not once per visit.
        app.MapGet("/api/analysis/{jobId}/voice", async Task<Results<Ok<VoiceProfileDto>, NotFound>> (
            string jobId, JobStore store, VoiceProfileService voices, CancellationToken ct) =>
        {
            if (!JobId.IsValid(jobId) || await store.LoadAsync(jobId, ct) is not { Stage: JobStage.Complete } state)
            {
                return TypedResults.NotFound();
            }
            // The demo's melody is a synthesized flute line: profiling it as a singer would present
            // an instrument's range as somebody's voice.
            if (state.Origin?.Kind == TakeOrigin.Demo)
            {
                return TypedResults.Ok(VoiceProfiler.Declined(
                    "This demo was synthesized, so there is no singer to profile."));
            }
            return TypedResults.Ok(VoiceProfiler.Build(await voices.IntonationAsync(state, ct), VoiceSubject.Song));
        })
        .WithTags("Analysis")
        .WithName("GetVoiceProfile")
        .WithSummary("The lead singer's voice type (by range) and the note they sing best, measured from the vocal.");

        return app;
    }
}
