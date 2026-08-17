using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PoMode.API.Features.Hardware;
using PoMode.API.Infrastructure;

namespace PoMode.Integration;

public class HardwareProbeTests
{
    private static HardwareProbe Probe(Dictionary<string, string?>? config = null)
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build();
        var modelRegistry = new ModelRegistry(
            configuration, provider.GetRequiredService<IHttpClientFactory>(), NullLogger<ModelRegistry>.Instance);
        return new HardwareProbe(
            configuration,
            provider.GetRequiredService<IHttpClientFactory>(),
            modelRegistry);
    }

    [Fact]
    public void Nvml_probe_never_throws_and_reports_nvidia_when_present()
    {
        var gpu = NvmlInterop.TryProbe(); // must not throw on machines without nvml.dll
        if (gpu is not null)
        {
            Assert.Equal("NVIDIA", gpu.Vendor);
            Assert.True(gpu.TotalVramMb > 0);
            Assert.True(gpu.FreeVramMb <= gpu.TotalVramMb);
            Assert.True(gpu.CudaAvailable);
        }
    }

    [Fact]
    public async Task Probe_reports_configured_providers_from_config()
    {
        var probe = Probe(new() { ["ReplicateApiToken"] = "x", ["SonicApiKey"] = "" });

        var report = await probe.ProbeAsync(CancellationToken.None);

        Assert.Contains("ReplicateApiToken", report.ConfiguredProviders);
        Assert.DoesNotContain("SonicApiKey", report.ConfiguredProviders);
        Assert.DoesNotContain("LalalApiKey", report.ConfiguredProviders);
    }

    [Fact]
    public async Task Probe_never_throws_when_ollama_is_unreachable()
    {
        // Whatever the machine state, ProbeAsync must complete and OllamaModels must be non-null.
        var report = await Probe().ProbeAsync(CancellationToken.None);

        Assert.NotNull(report.OllamaModels);
        Assert.False(report.IsAzureHosted);
    }
}
