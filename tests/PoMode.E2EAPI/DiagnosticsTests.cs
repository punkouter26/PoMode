using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using PoMode.Shared.Diagnostics;

namespace PoMode.E2EAPI;

public class DiagnosticsTests
{
    private const string FakeSecret = "sk-super-secret-value-9000";

    [Fact]
    public async Task Health_returns_healthy()
    {
        await using var factory = new WebApplicationFactory<Program>();
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
            await using var factory = new WebApplicationFactory<Program>();
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
        }
        finally
        {
            Environment.SetEnvironmentVariable("ReplicateApiToken", null);
        }
    }

    [Fact]
    public async Task OpenApi_document_and_scalar_ui_are_served()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var doc = await client.GetAsync("/openapi/v1.json");
        var scalar = await client.GetAsync("/scalar");

        doc.EnsureSuccessStatusCode();
        scalar.EnsureSuccessStatusCode();
    }
}
