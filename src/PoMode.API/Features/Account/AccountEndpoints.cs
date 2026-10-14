using System.IO.Compression;
using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.Auth;
using PoMode.API.Features.Push;

namespace PoMode.API.Features.Account;

/// <summary>
/// What the caller can do with everything stored under their name: take a copy, or have it gone.
/// A library is uploads and recordings of someone's voice, so both have to be one click rather than
/// an email to whoever runs the server.
/// </summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccount(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account").RequireAuthorization();

        // Every job folder the caller owns, as it is stored: the input audio, the stems, and each
        // artifact as JSON. Written to a temp file first because a zip needs to finish an entry before
        // the next, and the response then streams from disk and deletes the file when it closes.
        group.MapGet("/export", async Task<Results<FileStreamHttpResult, NotFound>> (
            HttpContext context, JobStore store, CancellationToken ct) =>
        {
            if (PoUser.IdOf(context.User) is not { } owner)
            {
                return TypedResults.NotFound();
            }
            var zipPath = Path.Combine(Path.GetTempPath(), $"pomode-export-{Guid.NewGuid():N}.zip");
            await using (var zipFile = File.Create(zipPath))
            await using (var zip = await ZipArchive.CreateAsync(zipFile, ZipArchiveMode.Create, leaveOpen: false, entryNameEncoding: null, ct))
            {
                foreach (var state in await OwnedJobsAsync(store, owner, ct))
                {
                    var dir = store.JobDir(state.JobId);
                    if (!Directory.Exists(dir))
                    {
                        continue;
                    }
                    var folder = $"{Path.GetFileNameWithoutExtension(SafeName(state.InputFileName))}-{state.JobId[..8]}";
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        await zip.CreateEntryFromFileAsync(file, $"{folder}/{Path.GetFileName(file)}", CompressionLevel.Fastest, ct);
                    }
                }
            }
            var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return TypedResults.File(stream, "application/zip", $"pomode-library-{DateTime.UtcNow:yyyyMMdd}.zip");
        });

        // Deletes every job the caller owns (cancelling any still running) and their push
        // subscriptions. The demo ledger keeps their name, so an emptied library stays empty rather
        // than greeting them with the demo again.
        group.MapDelete("/data", async Task<Results<Ok<int>, NotFound>> (
            HttpContext context, JobStore store, JobCancellationRegistry cancellations,
            PushSubscriptionStore push, CancellationToken ct) =>
        {
            if (PoUser.IdOf(context.User) is not { } owner)
            {
                return TypedResults.NotFound();
            }
            var deleted = 0;
            foreach (var state in await OwnedJobsAsync(store, owner, ct))
            {
                cancellations.TryCancel(state.JobId);
                if (store.TryDelete(state.JobId))
                {
                    deleted++;
                }
            }
            await push.RemoveAllAsync(owner, ct);
            return TypedResults.Ok(deleted);
        });

        return app;
    }

    private static async Task<List<JobState>> OwnedJobsAsync(JobStore store, string owner, CancellationToken ct)
    {
        var owned = new List<JobState>();
        foreach (var jobId in store.ListJobIds())
        {
            if (JobId.IsValid(jobId) && await store.LoadAsync(jobId, ct) is { } state && state.OwnerId == owner)
            {
                owned.Add(state);
            }
        }
        return owned;
    }

    private static string SafeName(string name)
    {
        var bare = Path.GetFileName(name.Replace('\\', '/'));
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            bare = bare.Replace(invalid, '_');
        }
        return bare.Length == 0 ? "upload" : bare;
    }
}
