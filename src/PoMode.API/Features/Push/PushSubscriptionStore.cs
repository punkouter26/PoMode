using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Account;

namespace PoMode.API.Features.Push;

/// <summary>One browser's subscription, as stored.</summary>
public sealed record StoredPushSubscription(string Endpoint, string P256dh, string Auth, DateTimeOffset CreatedAt);

/// <summary>
/// Push subscriptions per owner, persisted the way jobs are: a JSON file in a local folder
/// (<c>{jobs root}-push</c>) as the working copy, mirrored to the job blob container under a
/// <c>push-subscriptions/</c> prefix, and restored from there when the local file is gone — so a
/// restart or a new host does not silently stop every notification.
///
/// <para>Files are named by a hash of the owner id: ids carry a colon ("guest:…", "ms:…") that no
/// Windows file name may contain, and the hash keeps account ids out of blob names besides.</para>
/// </summary>
public sealed class PushSubscriptionStore(JobStore jobs, TimeProvider time, JobBlobStorage? blobs = null)
{
    /// <summary>The blob "folder" these files mirror into. Not a job id, so no job purge touches it.</summary>
    private const string BlobPrefix = "push-subscriptions";

    /// <summary>Enough for every browser one person plausibly uses. Past it the oldest goes, which
    /// bounds a file an endless stream of re-subscriptions could otherwise grow without limit.</summary>
    private const int MaxPerOwner = 10;

    /// <summary>One gate for every owner: subscribing is rare and each write is one small file.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string RootPath => jobs.SiblingPath("push");

    public async Task<IReadOnlyList<StoredPushSubscription>> ListAsync(string ownerId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ReadAsync(ownerId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds or refreshes one browser's subscription. The same endpoint subscribing again
    /// (a new session, a rotated key) replaces its earlier entry rather than duplicating it.</summary>
    public async Task AddAsync(string ownerId, PushSubscriptionDto subscription, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var kept = (await ReadAsync(ownerId, ct))
                .Where(existing => existing.Endpoint != subscription.Endpoint)
                .OrderByDescending(existing => existing.CreatedAt)
                .Take(MaxPerOwner - 1)
                .ToList();
            kept.Add(new StoredPushSubscription(
                subscription.Endpoint, subscription.P256dh, subscription.Auth, time.GetUtcNow()));
            await WriteAsync(ownerId, kept, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveAsync(string ownerId, string endpoint, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var existing = await ReadAsync(ownerId, ct);
            var kept = existing.Where(entry => entry.Endpoint != endpoint).ToList();
            if (kept.Count == existing.Count)
            {
                return false;
            }
            await WriteAsync(ownerId, kept, ct);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string FileNameOf(string ownerId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId))) + ".json";

    private async Task<List<StoredPushSubscription>> ReadAsync(string ownerId, CancellationToken ct)
    {
        var fileName = FileNameOf(ownerId);
        var path = Path.Combine(RootPath, fileName);
        if (!File.Exists(path))
        {
            if (blobs is null || await blobs.TryDownloadAsync(BlobPrefix, fileName, ct) is not { } restored)
            {
                return [];
            }
            Directory.CreateDirectory(RootPath);
            await File.WriteAllBytesAsync(path, restored, ct);
        }
        try
        {
            return JsonSerializer.Deserialize<List<StoredPushSubscription>>(
                await File.ReadAllTextAsync(path, ct), JobStore.JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // A torn file loses this owner's subscriptions, which the browser restores on its next
            // visit; failing the job that was about to notify them would lose more.
            return [];
        }
    }

    private async Task WriteAsync(string ownerId, List<StoredPushSubscription> subscriptions, CancellationToken ct)
    {
        var fileName = FileNameOf(ownerId);
        var path = Path.Combine(RootPath, fileName);
        if (subscriptions.Count == 0)
        {
            File.Delete(path);
            blobs?.DeleteBlob(BlobPrefix, fileName);
            return;
        }
        Directory.CreateDirectory(RootPath);
        var tempPath = path + ".tmp";
        await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(subscriptions, JobStore.JsonOptions), ct);
        File.Move(tempPath, path, overwrite: true);
        if (blobs is not null)
        {
            await blobs.MirrorFileAsync(BlobPrefix, fileName, path, ct);
        }
    }
}
