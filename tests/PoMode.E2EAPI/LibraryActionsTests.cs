using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EAPI;

/// <summary>
/// What a person can do with what is already in their library: keep a beat without analysing it,
/// run a finished song again from one stage, find songs like it, and take or delete everything.
/// Each test gets its own jobs folder, so the libraries they assert on hold only their own jobs.
/// </summary>
public sealed class LibraryActionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-library-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-library-models-{Guid.NewGuid():N}");

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

    private static async Task<JobStatusDto> FinishedAsync(HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        JobStatusDto? status;
        do
        {
            status = await client.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{jobId}");
            if (status!.Stage.IsTerminal())
            {
                return status;
            }
            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);
        return status!;
    }

    private static async Task<JobStatusDto> AnalyseAsync(HttpClient client, string name, double hz)
    {
        var created = await (await client.UploadAudioAsync(TestAudio.MakeTone(2.0, hz), name)).Content.ReadFromJsonAsync<JobStatusDto>();
        return await FinishedAsync(client, created!.JobId);
    }

    /// <summary>A beat is stored and listed but never planned or queued: complete at once, marked as a
    /// beat, its audio served back for the Mode Lab, and with no reading to compare or re-run.</summary>
    [Fact]
    public async Task A_beat_is_kept_without_being_analysed_and_cannot_be_rerun()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var beat = await (await client.UploadAudioAsync(TestAudio.MakeTone(2.0, 220), "beat.wav", "?beat=true"))
            .Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.Equal(JobStage.Complete, beat!.Stage);
        Assert.Empty(beat.Plan);
        Assert.Equal(TakeOrigin.Beat, beat.Origin?.Kind);

        var library = await client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library");
        Assert.Contains(library!, entry => entry.JobId == beat.JobId && entry.Origin?.Kind == TakeOrigin.Beat);
        (await client.GetAsync($"/api/analysis/{beat.JobId}/stems/mix")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"/api/analysis/{beat.JobId}/rerun?stage=ChordDetecting&executor=ViterbiChordRecognizer", null)).StatusCode);
    }

    /// <summary>"Try another model": a new job keeps what the stages before the chosen one produced,
    /// runs the chosen stage with the chosen executor and everything after it, and leaves the
    /// original as it was. Someone else's song cannot be re-run.</summary>
    [Fact]
    public async Task A_finished_song_reruns_from_one_stage_with_the_earlier_stages_kept()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var original = await AnalyseAsync(client, "song.wav", 440);
        Assert.Equal(JobStage.Complete, original.Stage);

        var rerun = await (await client.PostAsync(
            $"/api/analysis/{original.JobId}/rerun?stage=ChordDetecting&executor=ViterbiChordRecognizer", null))
            .Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.NotEqual(original.JobId, rerun!.JobId);
        Assert.Equal([StageNames.Separating, StageNames.PitchTracking], rerun.CompletedStages);

        var finished = await FinishedAsync(client, rerun.JobId);
        Assert.Equal(JobStage.Complete, finished.Stage);
        Assert.Equal("ViterbiChordRecognizer", finished.StageHistory!.Last(r => r.Stage == StageNames.ChordDetecting).Executor);
        Assert.Equal(original.StageHistory!.First(r => r.Stage == StageNames.PitchTracking).Executor,
            finished.StageHistory!.First(r => r.Stage == StageNames.PitchTracking).Executor);

        using var asSomeoneElse = new HttpRequestMessage(HttpMethod.Post,
            $"/api/analysis/{original.JobId}/rerun?stage=ChordDetecting&executor=ViterbiChordRecognizer");
        asSomeoneElse.Headers.Add("X-Fake-User", "someone-else");
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(asSomeoneElse)).StatusCode);
    }

    /// <summary>The readings a finished song gains after the fact: its band's chroma (twelve rows per
    /// frame), its lyrics (204 when none were read), and the other songs in the library like it.</summary>
    [Fact]
    public async Task A_finished_song_has_chroma_lyrics_state_and_songs_like_it()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var first = await AnalyseAsync(client, "first.wav", 440);
        var second = await AnalyseAsync(client, "second.wav", 440);

        var chroma = await client.GetFromJsonAsync<ChromaDto>($"/api/analysis/{first.JobId}/chroma");
        Assert.True(chroma!.Frames > 0);
        Assert.Equal(chroma.Frames * 12, Convert.FromBase64String(chroma.Energy).Length);

        var lyrics = await client.GetAsync($"/api/analysis/{first.JobId}/lyrics");
        Assert.True(lyrics.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK);

        // A held tone has no mode to compare, so only the shape of the answer is checked here; the
        // scoring is SongSimilarityTests'. Never the song itself, and every pick carries its reason.
        var similar = await client.GetFromJsonAsync<List<SimilarSongDto>>($"/api/library/{first.JobId}/similar");
        Assert.DoesNotContain(similar!, song => song.JobId == first.JobId);
        Assert.All(similar!, song => Assert.False(string.IsNullOrWhiteSpace(song.Reason)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/library/{new string('0', 32)}/similar")).StatusCode);
        Assert.Equal(JobStage.Complete, second.Stage);
    }

    /// <summary>Everything stored under one name, taken away whole or deleted whole, and only that
    /// name's: another user's songs survive the delete and are never in the export.</summary>
    [Fact]
    public async Task Export_holds_the_callers_jobs_and_delete_empties_only_their_library()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        var mine = await AnalyseAsync(client, "mine.wav", 330);
        using var someoneElse = factory.CreateClient();
        someoneElse.DefaultRequestHeaders.Remove("X-Fake-User");
        someoneElse.DefaultRequestHeaders.Add("X-Fake-User", "someone-else");
        var theirs = await AnalyseAsync(someoneElse, "theirs.wav", 330);

        var export = await client.GetAsync("/api/account/export");
        export.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        using (var zip = new ZipArchive(await export.Content.ReadAsStreamAsync()))
        {
            Assert.Contains(zip.Entries, entry => entry.FullName.EndsWith("job.json") && entry.FullName.Contains(mine.JobId[..8]));
            Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains(theirs.JobId[..8]));
        }

        var deleted = await (await client.DeleteAsync("/api/account/data")).Content.ReadFromJsonAsync<int>();
        Assert.True(deleted >= 1);
        Assert.Empty((await client.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/analysis/{mine.JobId}")).StatusCode);
        Assert.Contains((await someoneElse.GetFromJsonAsync<List<LibraryEntryDto>>("/api/library"))!, entry => entry.JobId == theirs.JobId);
    }
}
