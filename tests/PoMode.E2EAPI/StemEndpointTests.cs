using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class StemEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-stems-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-stems-models-{Guid.NewGuid():N}");

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

    private static async Task<string> CompletedJobAsync(HttpClient client)
    {
        var content = new ByteArrayContent(TestAudio.MakeWav());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var form = new MultipartFormDataContent { { content, "file", "test.wav" } };

        var created = await (await client.PostAsync("/api/analysis", form)).Content.ReadFromJsonAsync<JobStatusDto>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{created!.JobId}");
            if (status!.Stage is JobStage.Complete) return created.JobId;
            if (status.Stage is JobStage.Failed or JobStage.Cancelled)
            {
                throw new InvalidOperationException($"Job ended as {status.Stage}: {status.Error}");
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("Job did not complete in 15s.");
    }

    [Fact]
    public async Task Each_allow_listed_stem_is_served_as_playable_audio()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var jobId = await CompletedJobAsync(client);

        var mixResponse = await client.GetAsync($"/api/analysis/{jobId}/stems/mix");
        mixResponse.EnsureSuccessStatusCode();
        var mixBytes = await mixResponse.Content.ReadAsByteArrayAsync();
        Assert.True(mixBytes.Length > 0);

        var vocalResponse = await client.GetAsync($"/api/analysis/{jobId}/stems/vocals");
        vocalResponse.EnsureSuccessStatusCode();
        var vocalBytes = await vocalResponse.Content.ReadAsByteArrayAsync();
        Assert.True(vocalBytes.Length > 0);
    }

    [Fact]
    public async Task Unknown_and_unsupported_stem_names_are_rejected()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var jobId = await CompletedJobAsync(client);

        var badResponse = await client.GetAsync($"/api/analysis/{jobId}/stems/drums");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, badResponse.StatusCode);
    }
}
