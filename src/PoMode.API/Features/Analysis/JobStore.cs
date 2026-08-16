using System.Text.Json;

namespace PoMode.API.Features.Analysis;

/// <summary>Per-job folder persistence under Jobs:RootPath. The folder is the source of truth (no database).</summary>
public sealed class JobStore(IConfiguration configuration, TimeProvider time)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

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
        => await File.WriteAllTextAsync(StatePath(state.JobId), JsonSerializer.Serialize(state, JsonOptions), ct);

    public async Task<JobState?> LoadAsync(string jobId, CancellationToken ct)
    {
        var path = StatePath(jobId);
        if (!File.Exists(path))
        {
            return null;
        }
        return JsonSerializer.Deserialize<JobState>(await File.ReadAllTextAsync(path, ct), JsonOptions);
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
