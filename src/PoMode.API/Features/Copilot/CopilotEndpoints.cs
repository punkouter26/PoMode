using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Copilot;

public static class CopilotEndpoints
{
    public static IEndpointRouteBuilder MapCopilot(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/copilot");

        // Always returns 200 with a CopilotReply when the job and window exist, even with no Ollama
        // installed: an absent copilot is a normal state the UI renders, not a request failure.
        group.MapPost("/explain", async Task<Results<Ok<CopilotReply>, NotFound>> (
            CopilotRequest request, JobStore store, OllamaCopilotClient copilot, CancellationToken ct) =>
        {
            if (!JobId.IsValid(request.JobId))
            {
                return TypedResults.NotFound();
            }
            var result = await store.ReadArtifactAsync<ModalResult>(request.JobId, "result.json", ct);
            if (result is null)
            {
                return TypedResults.NotFound();
            }
            if (request.WindowIndex < 0 || request.WindowIndex >= result.Windows.Count)
            {
                return TypedResults.NotFound();
            }

            return TypedResults.Ok(
                await copilot.ExplainAsync(result, result.Windows[request.WindowIndex], ct));
        }).RequireAuthorization();

        return app;
    }
}
