using PoMode.API.Audio;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// How long a running analysis has left, from how long the same executors took on earlier songs on
/// this server. Each finished stage gives one sample: seconds of work per second of audio, for the
/// executor that really ran it. A job's estimate is its remaining stages' median rates times its own
/// length, less what the current stage has already spent. When any remaining executor has never been
/// timed here there is no estimate: a guess would be a figure the server did not measure.
/// </summary>
public sealed class EtaEstimator(JobStore store, TimeProvider time)
{
    /// <summary>The samples are re-read at most this often; a new song's stages join after that.</summary>
    private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
    private const int MaxJobsScanned = 200;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, double>? _rates;
    private DateTimeOffset _ratesAt;

    public async Task<EtaDto> EstimateAsync(JobState job, CancellationToken ct)
    {
        if (job.Stage.IsTerminal() || AudioDecoder.TryReadDurationSeconds(store.InputPath(job)) is not { } audioSec)
        {
            return new EtaDto(null);
        }
        var rates = await RatesAsync(ct);
        double left = 0;
        foreach (var plan in job.Plan.Where(p => !job.CompletedStages.Contains(p.Stage)))
        {
            if (!rates.TryGetValue(plan.Executor, out var rate))
            {
                return new EtaDto(null);
            }
            var stageSec = rate * audioSec;
            var running = job.StageHistory.LastOrDefault(r => r.Stage == plan.Stage && r.CompletedAt is null);
            if (running is not null)
            {
                stageSec = Math.Max(stageSec - (time.GetUtcNow() - running.StartedAt).TotalSeconds, 1);
            }
            left += stageSec;
        }
        return new EtaDto(Math.Round(left));
    }

    private async Task<Dictionary<string, double>> RatesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_rates is not null && time.GetUtcNow() - _ratesAt < Refresh)
            {
                return _rates;
            }
            var samples = new Dictionary<string, List<double>>();
            var recent = store.ListJobIds()
                .Where(JobId.IsValid)
                .OrderByDescending(id => Directory.GetLastWriteTimeUtc(store.JobDir(id)))
                .Take(MaxJobsScanned);
            foreach (var jobId in recent)
            {
                if (await store.LoadAsync(jobId, ct) is not { Stage: JobStage.Complete } done
                    || AudioDecoder.TryReadDurationSeconds(store.InputPath(done)) is not { } seconds
                    || seconds <= 1)
                {
                    continue;
                }
                foreach (var record in done.StageHistory)
                {
                    if (record.CompletedAt is { } end && end > record.StartedAt)
                    {
                        var list = samples.TryGetValue(record.Executor, out var existing) ? existing : samples[record.Executor] = [];
                        list.Add((end - record.StartedAt).TotalSeconds / seconds);
                    }
                }
            }
            _rates = samples.ToDictionary(pair => pair.Key, pair => Median(pair.Value));
            _ratesAt = time.GetUtcNow();
            return _rates;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}
