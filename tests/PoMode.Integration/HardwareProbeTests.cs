using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PoMode.API.Features.Diagnostics;
using PoMode.API.Infrastructure;

namespace PoMode.Integration;

public class HardwareProbeTests
{
    private static HardwareProbe Probe()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var modelRegistry = new ModelRegistry(
            configuration, provider.GetRequiredService<IHttpClientFactory>(), NullLogger<ModelRegistry>.Instance);
        return new HardwareProbe(modelRegistry);
    }

    [Fact]
    public async Task Probe_never_throws_when_optional_dependencies_are_unreachable()
    {
        // Whatever the machine state, ProbeAsync must complete without throwing.
        var report = await Probe().ProbeAsync(CancellationToken.None);

        Assert.False(report.IsAzureHosted);
    }
}
