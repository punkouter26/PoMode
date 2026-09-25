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

    private WebApplicationFactory<Program> Factory() => new AuthedFactory()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:RootPath", _modelsRoot)
            .UseSetting("Models:AutoDownload", "false"));

    private static async Task<string> CompletedJobAsync(HttpClient client)
    {
        var created = await (await client.UploadAudioAsync(TestAudio.MakeWav())).Content.ReadFromJsonAsync<JobStatusDto>();
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
        // No mode was named and no chord was heard: nothing to explain and nothing to divide, so both
        // derived readouts are absent rather than filled with a vacuous answer.
        Assert.Null(payload.Evidence);
        Assert.Empty(payload.Sections);
        Assert.All(payload.Notes, note =>
        {
            Assert.False(string.IsNullOrWhiteSpace(note.PitchLabel));
            Assert.StartsWith("[", note.DegreeLabel);
            Assert.EndsWith("]", note.DegreeLabel);
            Assert.InRange(note.MidiPitch, payload.MinPitch, payload.MaxPitch);
        });

        // Same silent job, the singer's profile: nothing was sung, so it declines in words rather
        // than naming a voice type — and an unknown job is a 404, like every other per-job read.
        var voice = await client.GetFromJsonAsync<VoiceProfileDto>($"/api/analysis/{jobId}/voice");
        Assert.NotNull(voice);
        Assert.Null(voice.Type);
        Assert.False(string.IsNullOrWhiteSpace(voice.Summary));
        var missing = await client.GetAsync($"/api/analysis/{new string('0', 32)}/voice");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);

        // No chords, so no sections and nothing to explain about them: the structure view is a 404,
        // like the ribbon's absence. The progress view's waveform sketch still answers, and silence
        // draws flat.
        var structure = await client.GetAsync($"/api/analysis/{jobId}/structure");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, structure.StatusCode);
        var peaks = await client.GetFromJsonAsync<WaveformPeaksDto>($"/api/analysis/{jobId}/peaks");
        Assert.NotNull(peaks);
        Assert.Contains(peaks.Source, new[] { "vocals", "mix" });
        Assert.Equal(1024, peaks.Peaks.Count);
        Assert.True(peaks.DurationSec > 0);
        Assert.All(peaks.Peaks, value => Assert.InRange(value, -0.01f, 0.01f));
    }
}
