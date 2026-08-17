using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class MidiExportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-midi-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-midi-models-{Guid.NewGuid():N}");

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
    public async Task Completed_job_exports_a_playable_midi_file()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var content = new ByteArrayContent(TestAudio.MakeWav());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var form = new MultipartFormDataContent { { content, "file", "test.wav" } };
        var created = await (await client.PostAsync("/api/analysis", form)).Content.ReadFromJsonAsync<JobStatusDto>();

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{created!.JobId}");
            if (status!.Stage is JobStage.Complete or JobStage.Failed) break;
            await Task.Delay(200);
        }

        var response = await client.GetAsync($"/api/analysis/{created!.JobId}/midi");

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("MThd"u8.ToArray(), bytes.Take(4).ToArray());
        Assert.True(bytes.Length > 100);
    }

    [Fact]
    public async Task Unknown_job_midi_is_404()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/nope/midi")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/api/analysis/00000000000000000000000000000000/midi")).StatusCode);
    }

    [Fact]
    public async Task Corrupt_result_json_is_404_not_500()
    {
        const string validJobId = "0123456789abcdef0123456789abcdef";
        var jobDir = Path.Combine(_root, validJobId);
        Directory.CreateDirectory(jobDir);
        await File.WriteAllTextAsync(Path.Combine(jobDir, "result.json"), "{ not json");

        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/analysis/{validJobId}/midi");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
