using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>Pipeline-side progress seam; the SignalR implementation lives with the hub (Task 8).</summary>
public interface IAnalysisNotifier
{
    Task PublishAsync(JobStatusDto status, CancellationToken ct);
}

/// <summary>
/// The other half of telling someone about their job. <see cref="IAnalysisNotifier"/> reaches an open
/// tab and nothing else; an analysis takes minutes, and the tab is usually closed by the time it
/// ends. This fires once per run, when a job reaches Complete or Failed — never for a cancellation,
/// which the owner asked for — with the persisted state, so the owner and headline facts are known.
/// </summary>
public interface IJobOutcomeNotifier
{
    Task NotifyAsync(JobState state, CancellationToken ct);
}
