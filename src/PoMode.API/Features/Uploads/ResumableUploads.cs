using System.Collections.Concurrent;
using System.Net;
using System.Text;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.Auth;
using PoMode.Shared.Analysis;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Models.Expiration;
using tusdotnet.Stores;

namespace PoMode.API.Features.Uploads;

/// <summary>What handing a finished upload to the analyzer came to.</summary>
public abstract record UploadHandoff
{
    public sealed record Started(JobStatusDto Status) : UploadHandoff;

    /// <summary>No such upload for this owner: never created, someone else's, or expired.</summary>
    public sealed record Missing : UploadHandoff;

    /// <summary>The bytes are not all here yet; the client should resume, not finalize.</summary>
    public sealed record Incomplete(long Offset, long Length) : UploadHandoff;

    public sealed record Rejected(UploadRejection Reason) : UploadHandoff;
}

/// <summary>
/// tus resumable uploads: the one way audio files reach the analyzer. A voice memo shared from a
/// phone is up to 100 MB over mobile data, and a single multipart POST that drops at 60 MB restarts
/// from zero; here the client asks how far the server got and carries on from there.
///
/// <para>Partial uploads live in <c>{jobs root}-uploads</c>, local to this instance and never
/// mirrored: they are working state for minutes, not a record. An upload untouched for
/// <see cref="Lifetime"/> expires and the sweep deletes it. The expiry is sliding, so a slow upload
/// that keeps making progress never expires under itself.</para>
///
/// <para>Finishing an upload does not start a job by itself. The client calls
/// <c>POST /api/analysis/uploads/{id}</c> with the same query parameters the old multipart upload
/// took, and gets the same <c>JobStatusDto</c> back — the tus protocol has no place to return one, and
/// the executor picks belong to the analysis rather than to the bytes.</para>
/// </summary>
public sealed class ResumableUploads(JobStore jobs, TimeProvider time, ILogger<ResumableUploads> logger)
{
    public const string Route = "/api/uploads";

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _handoffLocks = new();

    public string RootPath => jobs.SiblingPath("uploads");

    /// <summary>Upload id → job id, written before the upload's bytes are deleted. A finalize whose
    /// response was lost on a flaky connection is retried by the client, and the retry must return
    /// the job the first call started rather than a 404 for bytes that have already moved.</summary>
    private string HandedOffDir => Path.Combine(RootPath, "handed-off");

    private TusDiskStore StoreFor(string? ownerId)
    {
        Directory.CreateDirectory(RootPath);
        return new TusDiskStore(RootPath, deletePartialFilesOnConcat: true, TusDiskBufferSize.Default,
            new OwnerScopedFileIdProvider(ownerId));
    }

    /// <summary>Per request, because the store's id provider is scoped to the caller.</summary>
    public Task<DefaultTusConfiguration> ConfigureAsync(HttpContext context)
    {
        var ids = new OwnerScopedFileIdProvider(PoUser.IdOf(context.User));
        return Task.FromResult(new DefaultTusConfiguration
        {
            Store = StoreFor(PoUser.IdOf(context.User)),
            MaxAllowedUploadSizeInBytesLong = AudioFormatValidator.MaxBytes,
            Expiration = new SlidingExpiration(Lifetime),
            Events = new Events
            {
                // The id provider already refuses someone else's id, but tusdotnet reports that as a
                // 400 "invalid id", which tells the caller the id exists in some form. A 404 says
                // only what a never-issued id says.
                OnAuthorizeAsync = async authorizing =>
                {
                    if (authorizing.FileId is { Length: > 0 } fileId && !await ids.ValidateId(fileId))
                    {
                        authorizing.FailRequest(HttpStatusCode.NotFound);
                    }
                },
                OnBeforeCreateAsync = creating =>
                {
                    // The size cap is checked when the upload is created, so a 400 MB file is refused
                    // before a byte of it is sent. A deferred length would postpone that to the end.
                    if (creating.UploadLengthIsDeferred)
                    {
                        creating.FailRequest("Declare the upload's length when creating it.");
                    }
                    else if (creating.FileConcatenation is not null)
                    {
                        creating.FailRequest("Concatenated uploads are not supported.");
                    }
                    else if (!creating.Metadata.TryGetValue("filename", out var name) || name.HasEmptyValue)
                    {
                        creating.FailRequest("A 'filename' metadata entry is required.");
                    }
                    return Task.CompletedTask;
                },
            },
        });
    }

    /// <summary>Validates a finished upload and hands it to <see cref="AnalysisIntake"/>, then deletes
    /// the upload. Safe to call twice for the same id.</summary>
    public async Task<UploadHandoff> StartAnalysisAsync(
        string uploadId,
        string ownerId,
        AnalysisIntake intake,
        bool clientCanInfer,
        IReadOnlyDictionary<string, string> preferredExecutors,
        CancellationToken ct)
    {
        if (!await new OwnerScopedFileIdProvider(ownerId).ValidateId(uploadId))
        {
            return new UploadHandoff.Missing();
        }

        var gate = _handoffLocks.GetOrAdd(uploadId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var marker = Path.Combine(HandedOffDir, uploadId);
            if (File.Exists(marker))
            {
                var earlier = await jobs.LoadAsync((await File.ReadAllTextAsync(marker, ct)).Trim(), ct);
                return earlier is null ? new UploadHandoff.Missing() : new UploadHandoff.Started(earlier.ToDto());
            }

            var store = StoreFor(ownerId);
            if (!await store.FileExistAsync(uploadId, ct))
            {
                return new UploadHandoff.Missing();
            }
            var length = await store.GetUploadLengthAsync(uploadId, ct) ?? -1;
            var offset = await store.GetUploadOffsetAsync(uploadId, ct);
            if (length < 0 || offset != length)
            {
                return new UploadHandoff.Incomplete(offset, length);
            }

            var file = await store.GetFileAsync(uploadId, ct);
            UploadRejection? rejection;
            await using (var probe = await file.GetContentAsync(ct))
            {
                rejection = await AudioFormatValidator.ValidateAsync(probe, length, ct);
            }
            if (rejection is { } reason)
            {
                // Nothing will ever make these bytes acceptable, so they go now rather than at expiry.
                await store.DeleteFileAsync(uploadId, ct);
                return new UploadHandoff.Rejected(reason);
            }

            var metadata = await file.GetMetadataAsync(ct);
            var fileName = SafeFileName(metadata.TryGetValue("filename", out var name) ? name.GetString(Encoding.UTF8) : null);

            JobState state;
            await using (var content = await file.GetContentAsync(ct))
            {
                state = await intake.StartAsync(fileName, content, clientCanInfer, ct, preferredExecutors, ownerId: ownerId);
            }

            Directory.CreateDirectory(HandedOffDir);
            await File.WriteAllTextAsync(marker, state.JobId, ct);
            await store.DeleteFileAsync(uploadId, ct);
            return new UploadHandoff.Started(state.ToDto());
        }
        finally
        {
            gate.Release();
            // Anyone already waiting holds this semaphore and finds the marker; anyone later makes a
            // fresh one and finds the same marker. Dropping it keeps the map from growing forever.
            _handoffLocks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(uploadId, gate));
        }
    }

    /// <summary>Deletes expired partial uploads and old hand-off markers.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        if (!Directory.Exists(RootPath))
        {
            return 0;
        }
        var removed = await StoreFor(ownerId: null).RemoveExpiredFilesAsync(ct);
        if (Directory.Exists(HandedOffDir))
        {
            var cutoff = time.GetUtcNow().UtcDateTime - Lifetime;
            foreach (var marker in Directory.EnumerateFiles(HandedOffDir))
            {
                if (File.GetLastWriteTimeUtc(marker) < cutoff)
                {
                    try
                    {
                        File.Delete(marker);
                    }
                    catch (IOException ex)
                    {
                        logger.LogDebug(ex, "Could not delete upload marker {Marker}; the next sweep retries.", marker);
                    }
                }
            }
        }
        return removed;
    }

    /// <summary>The client's name for the file, reduced to a bare file name. It becomes the library
    /// row's title and supplies the stored input's extension, so no directory part may survive.</summary>
    private static string SafeFileName(string? raw)
    {
        var name = Path.GetFileName((raw ?? "").Replace('\\', '/').Trim());
        if (name.Length == 0)
        {
            return "upload";
        }
        return name.Length > 200 ? name[^200..] : name;
    }
}
