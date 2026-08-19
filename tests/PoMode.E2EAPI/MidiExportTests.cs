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

        // A real tone (not silence): YinPitchTracker transcribes it for real in this model-less
        // test host, so the exported MIDI carries actual notes worth asserting a body size on.
        var content = new ByteArrayContent(TestAudio.MakeTone(1.5, 440.0));
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
}
