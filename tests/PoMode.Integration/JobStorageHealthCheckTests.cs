using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using PoMode.API.Features.Analysis;
using PoMode.API.Infrastructure;

namespace PoMode.Integration;

public class JobStorageHealthCheckTests
{
    private static JobStore StoreWith(string rootPath) =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:RootPath"] = rootPath })
                .Build(),
            TimeProvider.System);

    [Fact]
    public async Task Writable_directory_is_healthy()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pomode-health-{Guid.NewGuid():N}");
        try
        {
            var check = new JobStorageHealthCheck(StoreWith(dir));
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, result.Status);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
