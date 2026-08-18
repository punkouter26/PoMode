using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class HealthEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-health-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-health-models-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));

    [Fact]
    public async Task Liveness_runs_no_checks_and_is_200()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Readiness_is_200_even_when_optional_dependencies_are_absent()
    {
        // An optional dependency down is Degraded, not Unhealthy — a normal local state
        // and must never 503 the app.
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        response.EnsureSuccessStatusCode();
    }
}
