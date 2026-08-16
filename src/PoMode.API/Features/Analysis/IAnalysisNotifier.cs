using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>Pipeline-side progress seam; the SignalR implementation lives with the hub (Task 8).</summary>
public interface IAnalysisNotifier
{
    Task PublishAsync(JobStatusDto status, CancellationToken ct);
}
