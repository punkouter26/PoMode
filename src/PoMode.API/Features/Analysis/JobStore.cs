using System.Collections.Concurrent;
using System.Text.Json;

namespace PoMode.API.Features.Analysis;

/// <summary>Per-job folder persistence under Jobs:RootPath. The folder is the working copy;
/// when <see cref="JobBlobStorage"/> is connected (Azurite in dev, Azure Storage in prod) every
/// write is mirrored to blob storage and reads fall back to it when the local file is gone.</summary>
public sealed class JobStore(IConfiguration configuration, TimeProvider time, JobBlobStorage? blobs = null)
{
    // Shared with BatchStore so both stores persist with the same shape.
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

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

    /// <summary>Every job folder currently on disk, newest knowledge left to the caller — the
    /// library endpoint loads each job's state (and restores from blob) through the normal reads.</summary>
    public IReadOnlyList<string> ListJobIds()
        => Directory.Exists(RootPath)
            ? [.. Directory.GetDirectories(RootPath).Select(Path.GetFileName).OfType<string>()]
            : [];

    /// <summary>
    /// The stored upload's path without needing the job state, or null when the job folder is gone
    /// (purged by the nightly sweep, or deleted mid-flight) — callers degrade instead of throwing.
    /// </summary>
    public string? TryFindInputPath(string jobId)
    {
        var jobDir = JobDir(jobId);
        return Directory.Exists(jobDir)
            ? Directory.EnumerateFiles(jobDir, "input.*").FirstOrDefault()
            : null;
    }

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

    public Task SaveAsync(JobState state, CancellationToken ct)
        => WriteArtifactAsync(state.JobId, "job.json", state, ct);

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

    /// <summary>
    /// Local path of an artifact, restoring it from the blob mirror first when the local copy is
    /// gone. For large binaries (stem WAVs) endpoints stream this path with range support instead
    /// of materialising the whole file in memory; those files are written once, before the job
    /// completes, so serving them outside the per-job lock is safe.
    /// </summary>
    public async Task<string?> GetArtifactPathAsync(string jobId, string fileName, CancellationToken ct)
    {
        var gate = LockFor(jobId);
        await gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(JobDir(jobId), fileName);
            return File.Exists(path) || await TryRestoreFromBlobAsync(jobId, fileName, path, ct) ? path : null;
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

            if (createdAt < cutoff && TryDeleteJobDir(dir))
            {
                purged++;
            }
        }
        return purged;
    }

    /// <summary>
    /// Deletes the oldest completed job folders until the store's total size fits the budget.
    /// The age sweep keeps the store fresh; this cap keeps it from eating the disk when many
    /// songs land inside one retention window (each job carries ~40 MB of stem WAVs). Jobs not
    /// yet in a terminal stage are never candidates.
    /// </summary>
    public int PurgeToSizeBudget(long maxTotalBytes)
    {
        if (!Directory.Exists(RootPath))
        {
            return 0;
        }

        var jobs = new List<(string Dir, DateTimeOffset CreatedAt, long Bytes, bool Terminal)>();
        foreach (var dir in Directory.GetDirectories(RootPath))
        {
            var statePath = Path.Combine(dir, "job.json");
            DateTimeOffset createdAt = File.GetLastWriteTimeUtc(dir);
            // A folder with unreadable state is old junk: eligible, dated by the folder itself.
            var terminal = true;
            try
            {
                if (File.Exists(statePath)
                    && JsonSerializer.Deserialize<JobState>(File.ReadAllText(statePath), JsonOptions) is { } state)
                {
                    createdAt = state.CreatedAt;
                    terminal = PoMode.Shared.Analysis.JobStageExtensions.IsTerminal(state.Stage);
                }
            }
            catch (JsonException)
            {
                // keep defaults
            }
            long bytes = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    bytes += new FileInfo(file).Length;
                }
            }
            catch (IOException)
            {
                // A file vanished mid-walk; the partial sum still orders correctly enough.
            }
            jobs.Add((dir, createdAt, bytes, terminal));
        }

        var total = jobs.Sum(job => job.Bytes);
        var purged = 0;
        foreach (var job in jobs.Where(j => j.Terminal).OrderBy(j => j.CreatedAt))
        {
            if (total <= maxTotalBytes)
            {
                break;
            }
            if (TryDeleteJobDir(job.Dir))
            {
                total -= job.Bytes;
                purged++;
            }
        }
        return purged;
    }

    /// <summary>One deletion path for every purge: folder, blob mirror, and the per-job lock.</summary>
    private bool TryDeleteJobDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A file can be open mid-stream (a stem being range-served) — skip this job
            // and let the next sweep retry rather than aborting the whole pass.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        var purgedJobId = Path.GetFileName(dir);
        blobs?.DeleteJobBlobs(purgedJobId);
        // The per-job lock has no job left to guard — drop it, or the dictionary grows
        // by one semaphore per job for the process lifetime.
        if (_locks.TryRemove(purgedJobId, out var gate))
        {
            gate.Dispose();
        }
        return true;
    }
}
