using PoMode.API.Infrastructure;
using PoMode.Shared.Diagnostics;

namespace PoMode.API.Features.Hardware;

/// <summary>Builds the /diag report. Reports secret PRESENCE only — never values. Phase 2 adds the GPU probe.</summary>
public sealed class DiagnosticsService(
    IConfiguration configuration,
    IHostEnvironment environment,
    SecretSourceInfo secretSource)
{
    private static readonly string[] ProviderKeyNames = ["ReplicateApiToken", "SonicApiKey", "LalalApiKey"];

    public DiagnosticsReport BuildReport() => new(
        EnvironmentName: environment.EnvironmentName,
        IsAzureHosted: IsAzureHosted(),
        SecretSource: secretSource.Source.ToString(),
        SecretFellBack: secretSource.FellBack,
        ProviderKeys: ProviderKeyNames
            .Select(name => new ProviderKeyStatus(name, !string.IsNullOrEmpty(configuration[name])))
            .ToArray(),
        Hardware: null);

    private static bool IsAzureHosted() =>
        Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") is not null
        || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
}
