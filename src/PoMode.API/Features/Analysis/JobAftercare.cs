using System.Threading.Channels;
using PoMode.API.Features.Lyrics;
using PoMode.API.Features.Push;
using PoMode.API.Features.SongStatistics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// Work worth doing for a finished song before anyone asks for it, so opening it is instant: the
/// written summary (the default interpreter's, which then sits in its cache) and the lyrics. One at a
/// time, in the background, after the analysis worker has moved on, because both are seconds to a
/// minute of model time that a person opening the song would otherwise wait through.
/// </summary>
/// <remarks>
/// Best-effort throughout: a failure is logged and the song simply does these on demand (the summary)
/// or not at all (the lyrics), as it did before. Beats, the server's own demo template and cancelled
/// or failed jobs are skipped; a sung take gets the summary but no lyrics, since humming has no words.
/// </remarks>
public sealed class JobAftercare(
    JobStore store,
    SongInterpreterSelector interpreters,
    LyricsTranscriber lyrics,
    ILogger<JobAftercare> logger) : BackgroundService
{
    private readonly Channel<string> _jobs = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(JobState state)
    {
        if (state.Stage == JobStage.Complete && !state.IsServerOwned && state.Origin?.Kind is not TakeOrigin.Beat)
        {
            _jobs.Writer.TryWrite(state.JobId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _jobs.Reader.ReadAllAsync(stoppingToken))
        {
            if (await store.LoadAsync(jobId, stoppingToken) is not { } state)
            {
                continue;
            }
            await TryAsync("summary", jobId, () => WriteSummaryAsync(jobId, stoppingToken), stoppingToken);
            if (state.Origin?.Kind is not TakeOrigin.HumTake)
            {
                await TryAsync("lyrics", jobId, () => lyrics.TranscribeAsync(state, stoppingToken), stoppingToken);
            }
        }
    }

    /// <summary>Runs the summary exactly as the page would ask for it, which writes it to the cache.</summary>
    private async Task WriteSummaryAsync(string jobId, CancellationToken ct)
    {
        if (await SongStatsEndpoints.BuildAsync(jobId, store, ct) is not { } stats)
        {
            return;
        }
        await foreach (var _ in interpreters.StreamAsync(jobId, stats, question: null, history: null, requested: null, ct))
        {
        }
    }

    private async Task TryAsync(string what, string jobId, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Writing the {What} ahead of time failed for job {JobId}.", what, jobId);
        }
    }
}

/// <summary>Everything that wants to hear a job finished: the owner's browsers (Web Push), and the
/// work done ahead of the owner opening it (<see cref="JobAftercare"/>).</summary>
public sealed class OutcomeFanOut(WebPushOutcomeNotifier push, JobAftercare aftercare) : IJobOutcomeNotifier
{
    public async Task NotifyAsync(JobState state, CancellationToken ct)
    {
        aftercare.Enqueue(state);
        await push.NotifyAsync(state, ct);
    }
}
