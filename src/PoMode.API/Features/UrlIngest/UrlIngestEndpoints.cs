using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.UrlIngest;

public static class UrlIngestEndpoints
{
    public static IEndpointRouteBuilder MapUrlIngest(this IEndpointRouteBuilder app)
    {
        // Requires auth: this endpoint spends server bandwidth and disk on an arbitrary URL.
        app.MapPost("/api/analysis/from-url", async Task<Results<Ok<JobStatusDto>, BadRequest<string>, ProblemHttpResult>> (
            AnalyzeUrlRequest request, UrlAudioService urls, AnalysisIntake intake,
            IConfiguration configuration, CancellationToken ct) =>
        {
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
            {
                return TypedResults.BadRequest("Enter an absolute http(s) URL.");
            }
            if (!configuration.GetValue("UrlIngest:AllowPrivateHosts", false)
                && await UrlHostGuard.PointsAtPrivateNetworkAsync(uri, ct))
            {
                return TypedResults.BadRequest("That URL points at a private or internal address.");
            }
            if (!await urls.IsAvailableAsync(ct))
            {
                return TypedResults.Problem(
                    "URL ingest is unavailable: yt-dlp is not installed on the server.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            string tempDir;
            string fileName;
            string filePath;
            try
            {
                (fileName, filePath, tempDir) = await urls.DownloadAsync(request.Url, ct);
            }
            catch (InvalidOperationException ex)
            {
                return TypedResults.BadRequest($"Could not fetch audio: {ex.Message}");
            }
            try
            {
                await using var stream = File.OpenRead(filePath);
                var state = await intake.StartAsync(fileName, stream, clientCanInfer: false, ct);
                return TypedResults.Ok(state.ToDto());
            }
            finally
            {
                try
                {
                    Directory.Delete(tempDir, recursive: true);
                }
                catch (IOException)
                {
                    // Temp cleanup is best-effort; the OS temp sweeper gets stragglers.
                }
            }
        }).RequireAuthorization();

        return app;
    }
}
