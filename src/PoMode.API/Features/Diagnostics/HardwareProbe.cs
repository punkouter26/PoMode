using PoMode.API.Infrastructure;
using PoMode.Shared.Hardware;

namespace PoMode.API.Features.Diagnostics;

/// <summary>Runtime capability probe feeding /diag and (in later phases) executor availability.</summary>
public sealed class HardwareProbe(ModelRegistry modelRegistry)
{
    public Task<HardwareReport> ProbeAsync(CancellationToken ct)
    {
        var isAzure = EnvironmentDetector.IsAzureHosted();
        var gpu = isAzure ? null : NvmlInterop.TryProbe();
        var models = modelRegistry.StatusFor(ModelCatalog.All);
        return Task.FromResult(new HardwareReport(isAzure, gpu, models));
    }
}
