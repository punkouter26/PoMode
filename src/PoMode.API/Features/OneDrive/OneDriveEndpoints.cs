using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Graph;
using Microsoft.Identity.Web;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.Auth;
using PoMode.Shared.Account;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.OneDrive;

/// <summary>
/// Songs from the person's own OneDrive, for someone signed in with Microsoft: a list of the audio
/// files in it, and each file's bytes streamed through this server so the browser can hand them to
/// the ordinary upload path (decoded to WAV where needed, sent over tus, analysed like any other).
/// </summary>
/// <remarks>
/// Not the removed URL ingest: that shelled out to a binary nothing installed. This is Microsoft
/// Graph over HTTP with the person's own delegated token, asked for only when they choose OneDrive
/// (incremental consent through <c>/auth/login/microsoft?onedrive=true</c>), read-only (Files.Read).
/// Bytes pass through and are not kept: the upload that follows stores them like any other.
/// </remarks>
public static class OneDriveEndpoints
{
    public const string Scope = "Files.Read";

    private static readonly string[] Extensions = ["mp3", "wav", "m4a"];
    private const int MaxFiles = 40;

    public static IEndpointRouteBuilder MapOneDrive(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/onedrive").RequireAuthorization();

        group.MapGet("/audio", async Task<Results<Ok<List<OneDriveFileDto>>, NotFound, UnauthorizedHttpResult>> (
            HttpContext context, CancellationToken ct) =>
        {
            if (GraphFor(context) is not { } graph)
            {
                return TypedResults.NotFound();
            }
            try
            {
                var drive = await graph.Me.Drive.GetAsync(cancellationToken: ct);
                if (drive?.Id is not { } driveId)
                {
                    return TypedResults.Ok(new List<OneDriveFileDto>());
                }
                var found = new Dictionary<string, OneDriveFileDto>();
                foreach (var extension in Extensions)
                {
                    var page = await graph.Drives[driveId].SearchWithQ(extension).GetAsSearchWithQGetResponseAsync(cancellationToken: ct);
                    foreach (var item in page?.Value ?? [])
                    {
                        if (item.Id is { } id && item.File is not null && item.Name is { } name
                            && name.EndsWith($".{extension}", StringComparison.OrdinalIgnoreCase)
                            && (item.Size ?? 0) <= AudioFormatValidator.MaxBytes)
                        {
                            found[id] = new OneDriveFileDto(id, name, item.Size ?? 0, item.LastModifiedDateTime);
                        }
                    }
                }
                return TypedResults.Ok(found.Values.OrderByDescending(file => file.Modified).Take(MaxFiles).ToList());
            }
            catch (Exception ex) when (NeedsConsent(ex))
            {
                return TypedResults.Unauthorized();
            }
        });

        group.MapGet("/items/{itemId}/content", async Task<Results<FileStreamHttpResult, NotFound, UnauthorizedHttpResult, BadRequest<string>>> (
            string itemId, HttpContext context, CancellationToken ct) =>
        {
            if (GraphFor(context) is not { } graph)
            {
                return TypedResults.NotFound();
            }
            try
            {
                var drive = await graph.Me.Drive.GetAsync(cancellationToken: ct);
                if (drive?.Id is not { } driveId
                    || await graph.Drives[driveId].Items[itemId].GetAsync(cancellationToken: ct) is not { File: not null } item)
                {
                    return TypedResults.NotFound();
                }
                if ((item.Size ?? 0) > AudioFormatValidator.MaxBytes)
                {
                    return TypedResults.BadRequest("File exceeds the 100 MB limit.");
                }
                var content = await graph.Drives[driveId].Items[itemId].Content.GetAsync(cancellationToken: ct);
                return content is null
                    ? TypedResults.NotFound()
                    : TypedResults.File(content, item.File.MimeType ?? "application/octet-stream", item.Name);
            }
            catch (Exception ex) when (NeedsConsent(ex))
            {
                return TypedResults.Unauthorized();
            }
        });

        return app;
    }

    /// <summary>Graph for a Microsoft sign-in on a server that has Microsoft sign-in; null otherwise.</summary>
    private static GraphServiceClient? GraphFor(HttpContext context)
        => context.RequestServices.GetRequiredService<PoAuthOptions>().MicrosoftAvailable
           && PoUser.KindOf(context.User) == SessionKind.Microsoft
            ? context.RequestServices.GetService<GraphServiceClient>()
            : null;

    /// <summary>No token for Files.Read yet (never consented, or the token cache was lost on a
    /// restart): the client sends the person through sign-in once more with the scope asked for.</summary>
    private static bool NeedsConsent(Exception ex)
        => ex is MicrosoftIdentityWebChallengeUserException
           || ex.InnerException is MicrosoftIdentityWebChallengeUserException
           || ex is Microsoft.Identity.Client.MsalUiRequiredException
           || ex.InnerException is Microsoft.Identity.Client.MsalUiRequiredException;
}
