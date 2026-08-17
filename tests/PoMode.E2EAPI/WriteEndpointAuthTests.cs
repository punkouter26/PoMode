using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

/// <summary>The write endpoints must refuse unauthenticated callers; reads stay open because the
/// unguessable 128-bit job id is the capability (and browser-native downloads carry no headers).</summary>
public sealed class WriteEndpointAuthTests : IDisposable
{
    private const string SomeJobId = "0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-auth-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-auth-models-{Guid.NewGuid():N}");

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
    public async Task Cancel_without_auth_is_401()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/api/analysis/{SomeJobId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Client_result_without_auth_is_401()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/analysis/{SomeJobId}/client-result", new List<NoteEvent>());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Copilot_without_auth_is_401()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/copilot/explain", new CopilotRequest(SomeJobId, 0));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task From_url_without_auth_is_401()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/analysis/from-url", new AnalyzeUrlRequest("https://example.com/watch?v=abc"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task From_url_rejects_a_malformed_url_with_400()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Fake-User", "e2e");

        var response = await client.PostAsJsonAsync(
            "/api/analysis/from-url", new AnalyzeUrlRequest("not a url"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
