using System.Text.Json;
using PoMode.API.Features.Analysis;

namespace PoMode.API.Features.Batch;

/// <summary>One batch: which jobs belong to it. Everything else lives in the jobs themselves.</summary>
public sealed record BatchManifest(string BatchId, DateTimeOffset CreatedAt, IReadOnlyList<BatchTrack> Tracks);

public sealed record BatchTrack(string JobId, string FileName);

/// <summary>
/// Persists batch manifests as files under Jobs:BatchPath (a sibling of the job folders, so the
/// job purge sweep never mistakes them for jobs), mirrored to blob storage like job artifacts.
/// The PoModeAg variant kept batches in memory and lost them on restart — these survive.
/// </summary>
public sealed class BatchStore(IConfiguration configuration, TimeProvider time, JobBlobStorage? blobs = null)
{
    private const string BlobPrefix = "batches";
    private static readonly JsonSerializerOptions JsonOptions = JobStore.JsonOptions;

    public string RootPath
    {
        get
        {
            var configured = configuration["Jobs:BatchPath"];
            return string.IsNullOrEmpty(configured)
                ? Path.Combine(AppContext.BaseDirectory, "batches")
                : configured;
        }
    }

    private string ManifestPath(string batchId) => Path.Combine(RootPath, batchId + ".json");

    public async Task<BatchManifest> CreateAsync(IReadOnlyList<BatchTrack> tracks, CancellationToken ct)
    {
        var manifest = new BatchManifest(Guid.NewGuid().ToString("N"), time.GetUtcNow(), tracks);
        Directory.CreateDirectory(RootPath);
        var path = ManifestPath(manifest.BatchId);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, JsonOptions), ct);
        if (blobs is not null)
        {
            await blobs.MirrorFileAsync(BlobPrefix, manifest.BatchId + ".json", path, ct);
        }
        return manifest;
    }

    public async Task<BatchManifest?> LoadAsync(string batchId, CancellationToken ct)
    {
        var path = ManifestPath(batchId);
        if (!File.Exists(path))
        {
            var restored = blobs is null ? null : await blobs.TryDownloadAsync(BlobPrefix, batchId + ".json", ct);
            if (restored is null)
            {
                return null;
            }
            Directory.CreateDirectory(RootPath);
            await File.WriteAllBytesAsync(path, restored, ct);
        }
        try
        {
            return JsonSerializer.Deserialize<BatchManifest>(await File.ReadAllTextAsync(path, ct), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public int PurgeOlderThan(TimeSpan maxAge)
    {
        if (!Directory.Exists(RootPath))
        {
            return 0;
        }
        var cutoff = time.GetUtcNow() - maxAge;
        var purged = 0;
        foreach (var path in Directory.GetFiles(RootPath, "*.json"))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff)
            {
                File.Delete(path);
                blobs?.DeleteBlob(BlobPrefix, Path.GetFileName(path));
                purged++;
            }
        }
        return purged;
    }
}
