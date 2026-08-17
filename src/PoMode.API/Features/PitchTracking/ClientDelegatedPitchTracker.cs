using PoMode.API.Features.Analysis;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// Tier 2 (spec §4): hands pitch tracking to the user's browser. Free, and the audio never leaves the
/// user's machine — which is why it sits above the paid cloud tier and below local inference.
///
/// <para>This executor does no inference. It parks: it registers a waiter, moves the job to
/// <see cref="JobStage.AwaitingClient"/> so the hub tells the browser to start, and awaits the notes
/// the browser posts to <c>client-result</c>. On timeout it throws, and
/// <c>AnalysisPipeline.RunWithFallbackAsync</c> falls through to the next tier — a browser that
/// crashes or walks away can never wedge a job.</para>
/// </summary>
public sealed class ClientDelegatedPitchTracker(
    ClientWorkRegistry registry,
    JobStore store,
    IAnalysisNotifier notifier,
    IConfiguration configuration,
    ILogger<ClientDelegatedPitchTracker> logger) : IPitchTracker
{
    private const int DefaultTimeoutSeconds = 300; // §4's five-minute guard

    public string Name => nameof(ClientDelegatedPitchTracker);
    public ExecutionTier Tier => ExecutionTier.ClientDelegated;

    /// <summary>
    /// Whether the feature is switched on at all. Whether a *particular* job's browser can actually do
    /// the work is a separate, per-job question that <see cref="ExecutionPlanner"/> answers — this
    /// interface has no job to inspect.
    /// </summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => Task.FromResult(configuration.GetValue("Tier2:Enabled", defaultValue: true));

    public async Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(
            configuration.GetValue("Tier2:TimeoutSeconds", DefaultTimeoutSeconds));

        // Register before announcing, so a very fast browser cannot post before anyone is waiting.
        var wait = registry.WaitAsync(context.JobId, timeout, ct);
        await AnnounceAsync(context.JobId, ct);

        logger.LogInformation("Job {JobId} is awaiting browser pitch tracking.", context.JobId);
        var notes = await wait;
        logger.LogInformation("Job {JobId} received {Count} notes from the browser.",
            context.JobId, notes.Count);
        return notes;
    }

    /// <summary>
    /// Publishes <see cref="JobStage.AwaitingClient"/> so the existing JobStatusChanged event tells the
    /// subscribed browser to start work (§13.5 collapsed §4's five named hub events into this one).
    /// </summary>
    private async Task AnnounceAsync(string jobId, CancellationToken ct)
    {
        var state = await store.LoadAsync(jobId, ct);
        if (state is null)
        {
            return;
        }
        state.Stage = JobStage.AwaitingClient;
        await store.SaveAsync(state, ct);
        await notifier.PublishAsync(state.ToDto(), ct);
    }
}
