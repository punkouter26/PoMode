using System.Collections.Concurrent;
using System.Text.Json;

namespace PoMode.API.Features.Analysis;

/// <summary>Per-job folder persistence under Jobs:RootPath. The folder is the source of truth (no database).</summary>
public sealed class JobStore(IConfiguration configuration, TimeProvider time)
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
            if (!File.Exists(path))
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
                purged++;
            }
        }
        return purged;
    }
}
