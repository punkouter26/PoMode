using Microsoft.AspNetCore.SignalR;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

public sealed class AnalysisHub(JobQueue queue) : Hub
{
    public const string QueuePositionMessage = "QueuePosition";

    public static string GroupName(string jobId) => $"job-{jobId}";

    /// <summary>Joins the job's group, and tells the caller where the job stands if it is still
    /// waiting: the pushes that follow only say when that changes.</summary>
    public async Task Subscribe(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(jobId));
        if (queue.Ahead(jobId) is { } ahead)
        {
            await Clients.Caller.SendAsync(QueuePositionMessage, jobId, ahead);
        }
    }
}

public sealed class SignalRAnalysisNotifier(IHubContext<AnalysisHub> hubContext) : IAnalysisNotifier
{
    public Task PublishAsync(JobStatusDto status, CancellationToken ct)
        => hubContext.Clients.Group(AnalysisHub.GroupName(status.JobId))
            .SendAsync("JobStatusChanged", status, ct);

    public Task PublishQueueAsync(string jobId, int ahead, CancellationToken ct)
        => hubContext.Clients.Group(AnalysisHub.GroupName(jobId))
            .SendAsync(AnalysisHub.QueuePositionMessage, jobId, ahead, ct);
}
