using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

public sealed class SongStatsInterpretationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-stats-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-stats-models-{Guid.NewGuid():N}");

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

    private static byte[] SongWav()
    {
        var tones = new List<(int Midi, double StartSec, double DurationSec, double Amplitude)>();
        int[] melody = [60, 62, 64, 65, 67, 69, 71, 72, 72, 71, 69, 67, 65, 64, 62, 60];
        for (var i = 0; i < melody.Length; i++)
        {
            tones.Add((melody[i], i * 0.25, 0.22, 0.8));
        }

        var chords = new (string Symbol, int[] Pitches, double StartSec, double DurationSec)[]
        {
            ("C", [48, 52, 55], 0.0, 1.0),
            ("G", [43, 47, 50], 1.0, 1.0),
            ("Am", [45, 48, 52], 2.0, 1.0),
            ("F", [41, 45, 48], 3.0, 1.0),
        };

        foreach (var (_, pitches, startSec, durationSec) in chords)
        {
            foreach (var pitch in pitches)
            {
                tones.Add((pitch, startSec, durationSec, 0.25));
            }
        }

        return TestAudio.MakeSong(4.0, tones);
    }

    private static async Task<string> AnalyseAsync(HttpClient client)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(SongWav());
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "test-song.wav");

        var response = await client.PostAsync("/api/analysis", content);
        response.EnsureSuccessStatusCode();
        var status = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.NotNull(status);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var poll = await client.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{status.JobId}");
            if (poll is { Stage: JobStage.Complete }) return status.JobId;
            if (poll is { Stage: JobStage.Failed }) throw new Exception($"Job failed: {poll.Error}");
            await Task.Delay(200);
        }

        throw new TimeoutException("Analysis did not complete within the test deadline.");
    }

    [Fact]
    public async Task Stats_and_fingerprint_are_derived_end_to_end_from_pipeline_run()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var jobId = await AnalyseAsync(client);
        var stats = await client.GetFromJsonAsync<SongStats>($"/api/analysis/{jobId}/stats");

        Assert.NotNull(stats);
        Assert.True(stats.MelodyNoteCount > 0);
        Assert.True(stats.ChordVocabulary.UniqueChords > 0);
        Assert.False(string.IsNullOrWhiteSpace(stats.Fingerprint));
        Assert.Contains(stats.TonicName, stats.Fingerprint);
    }

    [Fact]
    public async Task Interpretation_generates_valid_prose_and_handles_missing_jobs()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var notFound = await client.GetAsync($"/api/analysis/{Guid.NewGuid():N}/stats");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        var jobId = await AnalyseAsync(client);
        var interpretation = await client.GetFromJsonAsync<SongInterpretationDto>(
            $"/api/analysis/{jobId}/interpretation?interpreter=TemplateSongInterpreter");

        Assert.NotNull(interpretation);
        Assert.Equal("TemplateSongInterpreter", interpretation.Interpreter);
        Assert.Contains("For a singer:", interpretation.Text);
    }
}
