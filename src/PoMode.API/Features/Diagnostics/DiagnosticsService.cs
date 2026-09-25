using PoMode.API.Features.Analysis;
using PoMode.API.Features.Push;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.API.Platform;
using PoMode.Shared.Analysis;
using PoMode.Shared.Diagnostics;

namespace PoMode.API.Features.Diagnostics;

/// <summary>Builds the /diag report. Reports secret PRESENCE only — never values.</summary>
public sealed class DiagnosticsService(
    IHostEnvironment environment,
    SecretSourceInfo secretSource,
    HardwareProbe hardwareProbe,
    JobQueue queue,
    ExecutionPlanner planner,
    IConfiguration configuration,
    PushSettings push)
{
    public async Task<DiagnosticsReport> BuildReportAsync(CancellationToken ct) => new(
        EnvironmentName: environment.EnvironmentName,
        IsAzureHosted: EnvironmentDetector.IsAzureHosted(),
        SecretSource: secretSource.Source.ToString(),
        SecretFellBack: secretSource.FellBack,
        Hardware: await hardwareProbe.ProbeAsync(ct),
        QueueDepth: queue.Depth,
        DefaultPlan: await DefaultPlanAsync(ct),
        Operational: Operational());

    /// <summary>
    /// The guards this instance is running with. Presence only — in particular the OTLP endpoint is
    /// reported as a boolean, because that URL can carry credentials in a header and this payload
    /// redacts everything that could.
    /// </summary>
    private OperationalReport Operational() => new(
        RateLimitsEnabled: PoRateLimits.IsEnabled(configuration),
        MaxQueueDepth: configuration.GetValue("Jobs:MaxQueueDepth", 24),
        PushAvailable: push.Available);

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
