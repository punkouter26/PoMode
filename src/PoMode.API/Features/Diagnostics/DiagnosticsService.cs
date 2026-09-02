using PoMode.API.Features.Analysis;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;

namespace PoMode.API.Features.Diagnostics;

/// <summary>Builds the /diag report. Reports secret PRESENCE only — never values.</summary>
public sealed class DiagnosticsService(
    IHostEnvironment environment,
    SecretSourceInfo secretSource,
    HardwareProbe hardwareProbe,
    JobQueue queue,
    ExecutionPlanner planner)
{
    public async Task<DiagnosticsReport> BuildReportAsync(CancellationToken ct) => new(
        EnvironmentName: environment.EnvironmentName,
        IsAzureHosted: EnvironmentDetector.IsAzureHosted(),
        SecretSource: secretSource.Source.ToString(),
        SecretFellBack: secretSource.FellBack,
        Hardware: await hardwareProbe.ProbeAsync(ct),
        QueueDepth: queue.Depth,
        DefaultPlan: await DefaultPlanAsync(ct));

    private async Task<List<StagePlan>?> DefaultPlanAsync(CancellationToken ct)
    {
        try
        {
            return await planner.PlanAsync(ct);
        }
        catch (InvalidOperationException)
        {
            return null; // no executor set available — the report shows the gap as null
        }
    }
}
