using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using PoMode.Shared.Diagnostics;

namespace PoMode.E2EAPI;

public sealed class DiagnosticsTests : IDisposable
{
    private const string FakeSecret = "sk-super-secret-value-9000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:AutoDownload", "false"));

    [Fact]
    public async Task Health_returns_healthy()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
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
            Assert.False(report.ProviderKeys.Single(k => k.Provider == "SonicApiKey").Configured);
            Assert.False(report.ProviderKeys.Single(k => k.Provider == "LalalApiKey").Configured);
            Assert.False(report.IsAzureHosted);
            Assert.Equal("EnvironmentVariables", report.SecretSource);
            Assert.NotNull(report.Hardware);
            Assert.False(report.Hardware.IsAzureHosted);
            Assert.NotNull(report.Hardware.OllamaModels);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ReplicateApiToken", null);
        }
    }

    [Fact]
    public async Task OpenApi_document_and_scalar_ui_are_served()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var doc = await client.GetAsync("/openapi/v1.json");
        var scalar = await client.GetAsync("/scalar");

        doc.EnsureSuccessStatusCode();
        scalar.EnsureSuccessStatusCode();
    }
}
