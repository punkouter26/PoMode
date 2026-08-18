using PoMode.API.Features.Analysis;
using PoMode.API.Features.Cloud;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;

namespace PoMode.API.Features.Hardware;

/// <summary>Builds the /diag report. Reports secret PRESENCE only — never values.</summary>
public sealed class DiagnosticsService(
    IConfiguration configuration,
    IHostEnvironment environment,
    SecretSourceInfo secretSource,
    CloudCredentials cloudCredentials,
    HardwareProbe hardwareProbe,
    JobQueue queue,
    ExecutionPlanner planner)
{
    public async Task<DiagnosticsReport> BuildReportAsync(CancellationToken ct) => new(
        EnvironmentName: environment.EnvironmentName,
        IsAzureHosted: EnvironmentDetector.IsAzureHosted(),
        SecretSource: secretSource.Source.ToString(),
        SecretFellBack: secretSource.FellBack,
        ProviderKeys: ProviderKeys.All
            .Select(name => new ProviderKeyStatus(name, !string.IsNullOrEmpty(configuration[name])))
            .ToArray(),
        CloudEnabled: cloudCredentials.Enabled,
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
