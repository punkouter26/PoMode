using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class WebRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-webrt-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-webrt-models-{Guid.NewGuid():N}");

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

    private void Seed(string fileName, string content)
    {
        Directory.CreateDirectory(_modelsRoot);
        File.WriteAllText(Path.Combine(_modelsRoot, fileName), content);
    }

    [Fact]
    public async Task A_downloaded_runtime_asset_is_served_and_unlisted_files_are_blocked()
    {
        Seed("ort.all.bundle.min.mjs", "export const marker = 'pomode';");
        Seed("secrets.txt", "not-servable");
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/web-runtime/ort.all.bundle.min.mjs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);

        var blocked = await client.GetAsync("/web-runtime/secrets.txt");
        Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);
    }

    [Fact]
    public async Task Stage_executors_list_includes_client_delegated_browser_options()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var executors = await client.GetFromJsonAsync<List<StageExecutorsDto>>("/api/analysis/executors");
        Assert.NotNull(executors);
        Assert.NotEmpty(executors);
        Assert.Contains(executors, stage => stage.Executors.Any(e => e.Kind == ExecutorKind.Browser));
    }
}
