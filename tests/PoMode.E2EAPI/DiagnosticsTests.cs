using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using PoMode.Shared.Diagnostics;

namespace PoMode.E2EAPI;

public sealed class DiagnosticsTests : IDisposable
{
    private const string FakeSecret = "sk-super-secret-value-9000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-e2e-models-{Guid.NewGuid():N}");

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
    public async Task Health_returns_success()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        // Degraded is a 200 too: it means an optional dependency (the blob mirror)
        // is absent — a normal local state that must never read as an outage.
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(body, (string[])["Healthy", "Degraded"]);
    }

    [Fact]
    public async Task Diag_reports_provider_key_presence_without_leaking_values()
    {
        Environment.SetEnvironmentVariable("ReplicateApiToken", FakeSecret);
        try
        {
            await using var factory = Factory();
            using var client = factory.CreateClient();

            var raw = await client.GetStringAsync("/diag");
            var report = await client.GetFromJsonAsync<DiagnosticsReport>("/diag");

            Assert.DoesNotContain(FakeSecret, raw); // redaction is non-negotiable
            Assert.NotNull(report);
            Assert.True(report.ProviderKeys.Single(k => k.Provider == "ReplicateApiToken").Configured);
            Assert.False(report.ProviderKeys.Single(k => k.Provider == "LalalApiKey").Configured);
            // Sonic API was dropped in Phase 7 (the service no longer exists), so /diag must not
            // advertise a slot for it.
            Assert.DoesNotContain("SonicApiKey", report.ProviderKeys.Select(k => k.Provider));
            // A configured key with the tier enabled is the "paid fallback is armed" state.
            Assert.True(report.CloudEnabled);
            Assert.False(report.IsAzureHosted);
            Assert.Equal("EnvironmentVariables", report.SecretSource);
            Assert.NotNull(report.Hardware);
            Assert.False(report.Hardware.IsAzureHosted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ReplicateApiToken", null);
        }
    }
}
