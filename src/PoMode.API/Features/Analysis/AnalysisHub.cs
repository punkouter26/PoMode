using Microsoft.AspNetCore.SignalR;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

public sealed class AnalysisHub : Hub
{
    public static string GroupName(string jobId) => $"job-{jobId}";

    public Task Subscribe(string jobId)
        => Groups.AddToGroupAsync(Context.ConnectionId, GroupName(jobId));
}

public sealed class SignalRAnalysisNotifier(IHubContext<AnalysisHub> hubContext) : IAnalysisNotifier
{
    public Task PublishAsync(JobStatusDto status, CancellationToken ct)
        => hubContext.Clients.Group(AnalysisHub.GroupName(status.JobId))
            .SendAsync("JobStatusChanged", status, ct);
}
