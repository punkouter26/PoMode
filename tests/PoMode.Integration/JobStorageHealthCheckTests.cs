using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;
using PoMode.API.Infrastructure;

namespace PoMode.Integration;

public class JobStorageHealthCheckTests
{
    private static IConfiguration ConfigWith(string rootPath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:RootPath"] = rootPath })
            .Build();

    [Fact]
    public async Task Writable_directory_is_healthy()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pomode-health-{Guid.NewGuid():N}");
        try
        {
            var check = new JobStorageHealthCheck(ConfigWith(dir));
            var result = await check.CheckHealthAsync(new HealthCheckContext());
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Unwritable_path_is_unhealthy()
    {
        var check = new JobStorageHealthCheck(ConfigWith("Z:\\pomode-does-not-exist\\<>|invalid"));
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
