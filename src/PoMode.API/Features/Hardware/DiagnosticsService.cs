using PoMode.API.Infrastructure;
using PoMode.Shared.Diagnostics;

namespace PoMode.API.Features.Hardware;

/// <summary>Builds the /diag report. Reports secret PRESENCE only — never values.</summary>
public sealed class DiagnosticsService(
    IConfiguration configuration,
    IHostEnvironment environment,
    SecretSourceInfo secretSource,
    HardwareProbe hardwareProbe)
{
    private static readonly string[] ProviderKeyNames = ["ReplicateApiToken", "SonicApiKey", "LalalApiKey"];

    public async Task<DiagnosticsReport> BuildReportAsync(CancellationToken ct) => new(
        EnvironmentName: environment.EnvironmentName,
        IsAzureHosted: EnvironmentDetector.IsAzureHosted(),
        SecretSource: secretSource.Source.ToString(),
        SecretFellBack: secretSource.FellBack,
        ProviderKeys: ProviderKeyNames
            .Select(name => new ProviderKeyStatus(name, !string.IsNullOrEmpty(configuration[name])))
            .ToArray(),
        Hardware: await hardwareProbe.ProbeAsync(ct));
}
