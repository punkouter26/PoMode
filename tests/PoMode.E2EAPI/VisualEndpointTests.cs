using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class VisualEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-visual-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-visual-models-{Guid.NewGuid():N}");

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

    private static MultipartFormDataContent WavForm()
    {
        var content = new ByteArrayContent(TestAudio.MakeWav());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return new MultipartFormDataContent { { content, "file", "test.wav" } };
    }

    private static async Task<string> CompletedJobAsync(HttpClient client)
    {
        using var form = WavForm();
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
    public async Task A_completed_job_exposes_a_well_formed_visualization_payload()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var jobId = await CompletedJobAsync(client);

        var payload = await client.GetFromJsonAsync<VisualizationPayload>($"/api/analysis/{jobId}/visual");

        Assert.NotNull(payload);
        Assert.Equal(1, payload.SchemaVersion);
        // The fixture is silence and both real free executors run (YinPitchTracker for pitch,
        // ChromaChordRecognizer for chords), so zero notes, zero chords and zero duration are the
        // honest truth here — and the payload must still be well-formed around them.
        Assert.Empty(payload.Notes);
        Assert.Empty(payload.Chords);
        Assert.True(payload.MaxPitch - payload.MinPitch >= 12, $"range was {payload.MaxPitch - payload.MinPitch}");
        Assert.Equal(0.0, payload.DurationSec);
        Assert.All(payload.Notes, note =>
        {
            Assert.False(string.IsNullOrWhiteSpace(note.PitchLabel));
            Assert.StartsWith("[", note.DegreeLabel);
            Assert.EndsWith("]", note.DegreeLabel);
            Assert.InRange(note.MidiPitch, payload.MinPitch, payload.MaxPitch);
        });
    }

    [Fact]
    public async Task An_unknown_job_is_not_found()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/analysis/nope/visual")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/analysis/{new string('a', 32)}/visual")).StatusCode);
    }
}
