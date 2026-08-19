using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.E2EAPI;

/// <summary>
/// The browser-runtime asset endpoint and the upload-time capability declaration, over real HTTP.
/// Assets are pre-seeded on disk so no test touches the network; the download-on-miss path reuses
/// ModelRegistry.EnsureAsync, which has its own Integration coverage against a fixture server.
/// </summary>
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
    public async Task A_downloaded_runtime_asset_is_served_with_a_javascript_content_type()
    {
        Seed("ort.all.bundle.min.mjs", "export const marker = 'pomode';");
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/web-runtime/ort.all.bundle.min.mjs");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("pomode", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_asset_outside_the_allow_list_is_not_found()
    {
        Seed("secrets.txt", "not-servable");
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // On disk right next to the real assets, but not in the allow-list — the route value never
        // selects arbitrary files.
        var response = await client.GetAsync("/web-runtime/secrets.txt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_upload_that_declares_browser_capability_plans_the_client_delegated_tier()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var status = await UploadAsync(client, "/api/analysis?clientCanInfer=true");

        var pitch = status.Plan.Single(p => p.Stage == "PitchTracking");
        Assert.Equal(ExecutionTier.ClientDelegated, pitch.Tier);
        Assert.Equal("ClientDelegatedPitchTracker", pitch.Executor);
    }

    private static async Task<JobStatusDto> UploadAsync(HttpClient client, string url)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(PoMode.TestCommon.TestAudio.MakeWav(seconds: 0.2));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "test.wav");
        var response = await client.PostAsync(url, form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JobStatusDto>()
            ?? throw new InvalidOperationException("upload returned no status");
    }
}
