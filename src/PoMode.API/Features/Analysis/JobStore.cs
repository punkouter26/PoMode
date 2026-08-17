using System.Collections.Concurrent;
using System.Text.Json;

namespace PoMode.API.Features.Analysis;

/// <summary>Per-job folder persistence under Jobs:RootPath. The folder is the working copy;
/// when <see cref="JobBlobStorage"/> is connected (Azurite in dev, Azure Storage in prod) every
/// write is mirrored to blob storage and reads fall back to it when the local file is gone.</summary>
public sealed class JobStore(IConfiguration configuration, TimeProvider time, JobBlobStorage? blobs = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    // The API and worker share one process, so an in-process, per-job lock is sufficient to stop
    // a status-poll read (GET /{jobId}) from colliding with the pipeline's write of the same
    // job.json — without it, one side's File I/O throws IOException ("used by another process"),
    // which the pipeline's catch-all then reports as a hard job failure.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public string RootPath
    {
        get
        {
            var configured = configuration["Jobs:RootPath"];
            return string.IsNullOrEmpty(configured)
                ? Path.Combine(AppContext.BaseDirectory, "jobs")
                : configured;
        }
    }

    public string JobDir(string jobId) => Path.Combine(RootPath, jobId);

    public string InputPath(JobState state)
        => Path.Combine(JobDir(state.JobId), "input" + Path.GetExtension(state.InputFileName));

    private string StatePath(string jobId) => Path.Combine(JobDir(jobId), "job.json");

    private SemaphoreSlim LockFor(string jobId) => _locks.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));

    public async Task<JobState> CreateAsync(string fileName, Stream content, CancellationToken ct)
    {
        var state = new JobState
        {
            JobId = Guid.NewGuid().ToString("N"),
            InputFileName = fileName,
            CreatedAt = time.GetUtcNow(),
        };
        Directory.CreateDirectory(JobDir(state.JobId));
        await using (var file = File.Create(InputPath(state)))
        {
            await content.CopyToAsync(file, ct);
        }
        if (blobs is not null)
        {
            await blobs.MirrorFileAsync(state.JobId, Path.GetFileName(InputPath(state)), InputPath(state), ct);
        }
        await SaveAsync(state, ct);
        return state;
    }

    public async Task SaveAsync(JobState state, CancellationToken ct)
    {
        var gate = LockFor(state.JobId);
        await gate.WaitAsync(ct);
        try
        {
            var path = StatePath(state.JobId);
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(state, JsonOptions), ct);
            File.Move(tempPath, path, overwrite: true);
            if (blobs is not null)
            {
                await blobs.MirrorFileAsync(state.JobId, "job.json", path, ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<JobState?> LoadAsync(string jobId, CancellationToken ct)
    {
        var gate = LockFor(jobId);
        await gate.WaitAsync(ct);
        try
        {
            var path = StatePath(jobId);
            if (!File.Exists(path) && !await TryRestoreFromBlobAsync(jobId, "job.json", path, ct))
            {
                return null;
            }
            return JsonSerializer.Deserialize<JobState>(await File.ReadAllTextAsync(path, ct), JsonOptions);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task WriteArtifactAsync<T>(string jobId, string fileName, T payload, CancellationToken ct)
    {
        var gate = LockFor(jobId);
        await gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(JobDir(jobId), fileName);
            var tempPath = path + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(payload, JsonOptions), ct);
            File.Move(tempPath, path, overwrite: true);
            if (blobs is not null)
            {
                await blobs.MirrorFileAsync(jobId, fileName, path, ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<T>> ReadArtifactListAsync<T>(string jobId, string fileName, CancellationToken ct)
        => await ReadArtifactAsync<List<T>>(jobId, fileName, ct) ?? [];

    public async Task<T?> ReadArtifactAsync<T>(string jobId, string fileName, CancellationToken ct)
    {
        var gate = LockFor(jobId);
        await gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(JobDir(jobId), fileName);
            if (!File.Exists(path) && !await TryRestoreFromBlobAsync(jobId, fileName, path, ct))
            {
                return default;
            }
            return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, ct), JsonOptions);
        }
        catch (JsonException)
        {
            return default; // a torn or corrupt artifact is "not available", never a 500
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reads an artifact as bytes under the per-job lock so endpoints never stream a file mid-write.</summary>
    public async Task<byte[]?> ReadArtifactBytesAsync(string jobId, string fileName, CancellationToken ct)
    {
        var gate = LockFor(jobId);
        await gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(JobDir(jobId), fileName);
            if (!File.Exists(path) && !await TryRestoreFromBlobAsync(jobId, fileName, path, ct))
            {
                return null;
            }
            return await File.ReadAllBytesAsync(path, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Mirrors every file in the job folder to blob storage. Stem executors write
    /// .wav files straight into the folder, bypassing WriteArtifactAsync's own mirroring.</summary>
    public async Task MirrorToBlobAsync(string jobId, CancellationToken ct)
    {
        if (blobs is null || !Directory.Exists(JobDir(jobId)))
        {
            return;
        }
        var gate = LockFor(jobId);
        await gate.WaitAsync(ct);
        try
        {
            await blobs.MirrorDirectoryAsync(jobId, JobDir(jobId), ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> TryRestoreFromBlobAsync(string jobId, string fileName, string path, CancellationToken ct)
    {
        if (blobs is null)
        {
            return false;
        }
        var bytes = await blobs.TryDownloadAsync(jobId, fileName, ct);
        if (bytes is null)
        {
            return false;
        }
        Directory.CreateDirectory(JobDir(jobId));
        await File.WriteAllBytesAsync(path, bytes, ct);
        return true;
    }

    public int PurgeOlderThan(TimeSpan maxAge)
    {
        if (!Directory.Exists(RootPath))
        {
            return 0;
        }

        var cutoff = time.GetUtcNow() - maxAge;
        var purged = 0;
        foreach (var dir in Directory.GetDirectories(RootPath))
        {
            var statePath = Path.Combine(dir, "job.json");
            DateTimeOffset createdAt;
            try
            {
                createdAt = File.Exists(statePath)
                    ? JsonSerializer.Deserialize<JobState>(File.ReadAllText(statePath), JsonOptions)?.CreatedAt
                      ?? File.GetLastWriteTimeUtc(dir)
                    : File.GetLastWriteTimeUtc(dir);
            }
            catch (JsonException)
            {
                createdAt = File.GetLastWriteTimeUtc(dir);
            }

            if (createdAt < cutoff)
            {
                Directory.Delete(dir, recursive: true);
                blobs?.DeleteJobBlobs(Path.GetFileName(dir));
                purged++;
            }
        }
        return purged;
    }
}
