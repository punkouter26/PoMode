using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

/// <summary>
/// The hum round trip: a sung melody plus the progression it was sung over go in, and the job that
/// comes back has the singer's notes analyzed against that progression's chords. The point of the
/// endpoint is that the harmony travels with the take — a solo voice contains none for a recognizer
/// to find, so a job that arrived without its chords would be a mode scored over silence.
/// </summary>
public sealed class HumTakeEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-e2e-hum-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-e2e-hum-models-{Guid.NewGuid():N}");

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

    /// <summary>A take long enough to span several passes of an 8-bar loop at 120 BPM.</summary>
    private static MultipartFormDataContent TakeOf(double seconds)
    {
        var content = new MultipartFormDataContent();
        // A steady tone stands in for a hum: one voice, no harmony, which is exactly the property
        // that makes the seeded chord track necessary.
        var part = new ByteArrayContent(TestAudio.MakeTone(seconds, frequencyHz: 220.0));
        part.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(part, "file", "hum.wav");
        return content;
    }

    private static string Url(double targetPurity = 90.0, string progressionId = "pop-axis")
        => $"/api/modal-melodies/hum?tonicPitchClass=0&mode=Dorian&progressionId={progressionId}"
         + $"&bpm=120&style=Lyrical&seed=42&targetPurity={targetPurity.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    [Fact]
    public async Task A_hum_take_is_queued_carrying_the_progression_it_was_sung_over()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var take = TakeOf(seconds: 12.0);
        var response = await client.PostAsync(Url(), take);
        response.EnsureSuccessStatusCode();

        var job = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.NotNull(job);
        Assert.False(string.IsNullOrWhiteSpace(job.JobId));

        // Chord detection never runs for a hum: the chords are a setting the singer chose, and the
        // plan has to say so rather than crediting the recognizer that was scheduled.
        Assert.Contains(StageNames.ChordDetecting, job.CompletedStages);
        Assert.Contains(StageNames.Separating, job.CompletedStages);
        Assert.Equal("ModeLabBacking", job.Plan.Single(p => p.Stage == StageNames.ChordDetecting).Executor);
        Assert.Equal("SkippedDryVocal", job.Plan.Single(p => p.Stage == StageNames.Separating).Executor);

        // Nothing fake ran, so the client must not raise the mock-data banner over a real take.
        Assert.DoesNotContain(job.Plan, p => p.IsPlaceholder && p.Stage == StageNames.ChordDetecting);

        // The seeded chord track is on the job before the worker could have touched it, and it
        // repeats across the whole take rather than covering only the first pass.
        var chords = await client.GetFromJsonAsync<List<ChordSpan>>($"/api/analysis/{job.JobId}/chords");
        Assert.NotNull(chords);
        Assert.NotEmpty(chords);
        Assert.Equal(0.0, chords[0].StartSec, precision: 3);
        Assert.True(chords[^1].EndSec > 8.0, "the chords must cover the later passes the singer hummed over");
        Assert.True(chords[^1].EndSec <= 12.01, "the chords must not run past the end of the recording");

        // The tempo came off the slider, so it is known rather than estimated.
        var beats = await client.GetFromJsonAsync<BeatGridDto>($"/api/analysis/{job.JobId}/beats");
        Assert.NotNull(beats);
        Assert.Equal(120.0, beats.Bpm, precision: 3);
        Assert.True(beats.Confidence > 0);
    }

    /// <summary>
    /// The take says what it is. Without this the analyzer reports a mode on an unexplained file —
    /// and the sentence must describe the backing the singer heard, never assert a mode for the voice,
    /// which is the very thing the analysis is there to discover.
    /// </summary>
    [Fact]
    public async Task A_hum_take_carries_the_progression_it_was_sung_over_as_its_provenance()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var take = TakeOf(seconds: 9.0);
        var job = await (await client.PostAsync(Url(), take)).Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.NotNull(job);

        Assert.NotNull(job.Origin);
        Assert.Equal(TakeOrigin.HumTake, job.Origin.Kind);
        Assert.Contains("Hummed over", job.Origin.Description);
        Assert.Contains("120 BPM", job.Origin.Description);
        // The request named pop-axis, so that progression must be the one reported.
        Assert.Contains("Axis", job.Origin.Description, StringComparison.OrdinalIgnoreCase);

        // It survives the round trip to disk and back — the library reads it from job.json.
        var library = await client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library");
        Assert.NotNull(library);
        var row = library.Single(entry => entry.JobId == job.JobId);
        Assert.NotNull(row.Origin);
        Assert.Equal(job.Origin.Description, row.Origin.Description);
    }

    [Fact]
    public async Task An_ordinary_upload_has_no_provenance_to_report()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(TestAudio.MakeTone(2.0, frequencyHz: 220.0));
        part.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(part, "file", "song.wav");
        var job = await (await client.PostAsync("/api/analysis", content)).Content.ReadFromJsonAsync<JobStatusDto>();

        Assert.NotNull(job);
        // A dropped file explains itself; inventing an origin for one would be noise.
        Assert.Null(job.Origin);
    }

    /// <summary>
    /// Purity 0 is a real setting at the bottom of the slider, not an absent parameter: it rotates
    /// the progression off its home chord. Read as absent it would default to 90 and the take would
    /// be filed under a different opening chord than the singer actually heard.
    /// </summary>
    [Fact]
    public async Task Purity_zero_is_honoured_rather_than_read_as_an_absent_parameter()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        using var loose = TakeOf(seconds: 9.0);
        var looseJob = await (await client.PostAsync(Url(targetPurity: 0.0), loose))
            .Content.ReadFromJsonAsync<JobStatusDto>();
        using var tight = TakeOf(seconds: 9.0);
        var tightJob = await (await client.PostAsync(Url(targetPurity: 100.0), tight))
            .Content.ReadFromJsonAsync<JobStatusDto>();

        Assert.NotNull(looseJob);
        Assert.NotNull(tightJob);

        var looseChords = await client.GetFromJsonAsync<List<ChordSpan>>($"/api/analysis/{looseJob.JobId}/chords");
        var tightChords = await client.GetFromJsonAsync<List<ChordSpan>>($"/api/analysis/{tightJob.JobId}/chords");
        Assert.NotNull(looseChords);
        Assert.NotNull(tightChords);

        // Same progression, same key — the only difference is where the loop opens.
        Assert.NotEqual(tightChords[0].Symbol, looseChords[0].Symbol);
    }

    [Fact]
    public async Task A_hum_post_without_auth_is_401()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b
                .UseSetting("Jobs:RootPath", _root)
                .UseSetting("Models:RootPath", _modelsRoot)
                .UseSetting("Models:AutoDownload", "false"));
        using var client = factory.CreateClient();

        using var take = TakeOf(seconds: 2.0);
        var response = await client.PostAsync(Url(), take);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
