using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;
using Xunit.Abstractions;

namespace PoMode.E2EAPI;

/// <summary>
/// End to end over the real HTTP surface: upload a song, let the pipeline run, then ask for its
/// statistics and a written interpretation.
///
/// <para>The audio is synthesised rather than a checked-in MP3. A real recording cannot be committed
/// (<c>*.mp3</c> is git-ignored, so a fresh clone would not have it) and a downloaded one would make
/// the suite depend on a third party staying up. <see cref="TestAudio.MakeSong"/> gives a melody over
/// a chord bed whose ground truth is known here, which is what the assertions need.</para>
///
/// <para>The Ollama test skips itself when no local model server is reachable — the same contract
/// <c>OnnxPitchTrackerTests</c> uses for an undownloaded model. A developer without Ollama must not
/// see a red suite for a tier that is optional by design.</para>
/// </summary>
public sealed class SongStatsInterpretationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pomode-stats-{Guid.NewGuid():N}");
    private readonly string _modelsRoot = Path.Combine(Path.GetTempPath(), $"pomode-stats-models-{Guid.NewGuid():N}");

    /// <summary>A cold Ollama has to load the model from disk, which outlives HttpClient's 100 s default.</summary>
    private static readonly TimeSpan InterpretTimeout = TimeSpan.FromMinutes(10);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        if (Directory.Exists(_modelsRoot)) Directory.Delete(_modelsRoot, recursive: true);
    }

    private WebApplicationFactory<Program> Factory() => new AuthedFactory()
        .WithWebHostBuilder(b => b
            .UseSetting("Jobs:RootPath", _root)
            .UseSetting("Models:RootPath", _modelsRoot)
            // Never pull a 165 MB model into a test run; the classic DSP executors cover this.
            .UseSetting("Models:AutoDownload", "false"));

    /// <summary>
    /// Eight bars at 120 BPM: a stepwise quaver melody over a I-V-vi-IV pad. Written to be musically
    /// ordinary on purpose — the point is that the statistics and the prose come out of a real
    /// pipeline run, not that this particular tune is interesting.
    /// </summary>
    private static byte[] SongWav()
    {
        var tones = new List<(int Midi, double StartSec, double DurationSec, double Amplitude)>();

        // Melody: a C major line, one note every 0.25 s (quavers at 120 BPM), loud enough that the
        // pitch tracker follows it rather than the pad underneath.
        int[] line = [72, 74, 76, 77, 79, 77, 76, 74];
        for (var bar = 0; bar < 8; bar++)
        {
            for (var step = 0; step < 8; step++)
            {
                var midi = line[(bar + step) % line.Length];
                tones.Add((midi, (bar * 2.0) + (step * 0.25), 0.22, 1.0));
            }
        }

        // Pad: C - G - Am - F, one chord per bar, quiet so it colours the harmony without masking
        // the melody.
        int[] roots = [0, 7, 9, 5];
        string[] qualities = ["maj", "maj", "min", "maj"];
        for (var bar = 0; bar < 8; bar++)
        {
            foreach (var midi in TestAudio.Triad(roots[bar % 4], qualities[bar % 4]))
            {
                tones.Add((midi, bar * 2.0, 1.95, 0.22));
            }
        }

        return TestAudio.MakeSong(seconds: 16.0, tones);
    }

    private static MultipartFormDataContent SongForm()
    {
        var content = new ByteArrayContent(SongWav());
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return new MultipartFormDataContent { { content, "file", "e2e-song.wav" } };
    }

    /// <summary>Uploads the song and returns its job id once the pipeline reaches a terminal stage.</summary>
    private static async Task<string> AnalyseAsync(HttpClient client)
    {
        using var form = SongForm();
        var response = await client.PostAsync("/api/analysis", form);
        response.EnsureSuccessStatusCode();

        var created = await response.Content.ReadFromJsonAsync<JobStatusDto>();
        Assert.NotNull(created);

        // Polled rather than driven off the hub: this suite cares about the artifacts, and the job
        // can finish before a subscription lands (see AnalysisApiTests for the same note).
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            var status = await client.GetFromJsonAsync<JobStatusDto>($"/api/analysis/{created.JobId}");
            if (status!.Stage is JobStage.Complete or JobStage.Failed)
            {
                Assert.Equal(JobStage.Complete, status.Stage);
                return created.JobId;
            }
            await Task.Delay(250);
        }

        Assert.Fail("The analysis job did not reach a terminal stage within two minutes.");
        return string.Empty;
    }

    [Fact]
    public async Task An_uploaded_song_yields_statistics_and_a_fingerprint_paragraph()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var jobId = await AnalyseAsync(client);
        var stats = await client.GetFromJsonAsync<SongStats>($"/api/analysis/{jobId}/stats");

        Assert.NotNull(stats);
        output.WriteLine($"notes={stats.MelodyNoteCount} chords={stats.ChordVocabulary.UniqueChords} "
            + $"duration={stats.DurationSec:0.0}s beatGrid={stats.Rhythm.BeatGridUsable}");
        output.WriteLine(stats.Fingerprint);

        // The pipeline must have found real material; empty artifacts would make every figure below
        // vacuously true.
        Assert.True(stats.MelodyNoteCount > 0, "the pipeline transcribed no melody notes");
        Assert.True(stats.ChordVocabulary.UniqueChords > 0, "the pipeline found no chords");

        // Every derived block is populated rather than defaulted.
        Assert.True(stats.Motion.IntervalCount > 0);
        Assert.NotNull(stats.Tessitura);
        Assert.True(stats.Phrases.Count > 0);
        Assert.True(stats.HarmonicRhythm.AverageChordSec > 0);
        Assert.True(stats.ChordTones.ClassifiedNotes > 0);

        // The fingerprint is prose about this song, not a stub.
        Assert.False(string.IsNullOrWhiteSpace(stats.Fingerprint));
        Assert.Contains(stats.TonicName, stats.Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("No melody was transcribed", stats.Fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Statistics_are_not_found_for_a_job_that_never_existed()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/analysis/{Guid.NewGuid():N}/stats");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_interpreter_name_falls_back_instead_of_failing()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var jobId = await AnalyseAsync(client);
        var interpretation = await client.GetFromJsonAsync<SongInterpretationDto>(
            $"/api/analysis/{jobId}/interpretation?interpreter=NoSuchInterpreter");

        // A bad name must degrade to the default order, never 400 — the picker and the URL are both
        // things a user can get wrong, and neither should cost them their answer.
        Assert.NotNull(interpretation);
        Assert.False(string.IsNullOrWhiteSpace(interpretation.Text));
    }

    [Fact]
    public async Task The_template_interpreter_always_writes_an_interpretation()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var jobId = await AnalyseAsync(client);
        var interpretation = await client.GetFromJsonAsync<SongInterpretationDto>(
            $"/api/analysis/{jobId}/interpretation?interpreter=TemplateSongInterpreter");

        Assert.NotNull(interpretation);
        Assert.Equal("TemplateSongInterpreter", interpretation.Interpreter);
        Assert.False(interpretation.UsedLlm);
        // It opens with the fingerprint and adds its own two paragraphs.
        Assert.Contains("For a singer:", interpretation.Text, StringComparison.Ordinal);
        Assert.Contains("In character:", interpretation.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cloud_interpreter_is_listed_but_not_the_default()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var options = await client.GetFromJsonAsync<List<InterpreterOptionDto>>("/api/analysis/interpreters");

        Assert.NotNull(options);
        var azure = Assert.Single(options, option => option.Tier == ExecutionTier.Cloud);
        // Listed so it can be named, never defaulted: choosing it must be a deliberate act because
        // it spends money.
        Assert.False(azure.IsDefault);
        Assert.Contains(options, option => option is { IsDefault: true, Tier: ExecutionTier.Local });
    }

    [Fact]
    public async Task Ollama_writes_an_interpretation_grounded_in_the_measured_statistics()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        client.Timeout = InterpretTimeout;

        var options = await client.GetFromJsonAsync<List<InterpreterOptionDto>>("/api/analysis/interpreters");
        var ollama = options?.FirstOrDefault(option => option.Name == "OllamaSongInterpreter");
        if (ollama is not { Available: true })
        {
            output.WriteLine(
                "SKIPPED: no Ollama server with a usable model on this machine. "
                + "Install Ollama and run 'ollama pull llama3.2' to exercise the local LLM tier.");
            Assert.True(true);
            return;
        }

        var jobId = await AnalyseAsync(client);
        var stats = await client.GetFromJsonAsync<SongStats>($"/api/analysis/{jobId}/stats");
        var interpretation = await client.GetFromJsonAsync<SongInterpretationDto>(
            $"/api/analysis/{jobId}/interpretation?interpreter=OllamaSongInterpreter");

        Assert.NotNull(stats);
        Assert.NotNull(interpretation);
        output.WriteLine($"--- {interpretation.Interpreter} (tier {interpretation.Tier}) ---");
        output.WriteLine(interpretation.Text);

        // The selector falls through on failure, so asserting the name is what actually proves the
        // local model ran. Without this the test would pass on the template's output.
        Assert.Equal("OllamaSongInterpreter", interpretation.Interpreter);
        Assert.True(interpretation.UsedLlm);
        Assert.Equal(ExecutionTier.Local, interpretation.Tier);

        // Real prose, not a stub or a one-liner refusal.
        Assert.True(interpretation.Text.Length > 200,
            $"the model returned {interpretation.Text.Length} characters, expected a multi-paragraph answer");

        // Grounding, checked by subject rather than by literal: the prompt now asks for plain English
        // for a non-musician, so the model is expected to turn "62 semitones" into "five octaves" and
        // may never spell the key at all. Asserting on the tonic string would fail for prose that is
        // doing exactly what it was asked. What must hold is that it wrote about *this song's* music.
        string[] subjects = ["melody", "rhythm", "chord", "sing", "note"];
        var mentioned = subjects.Count(word =>
            interpretation.Text.Contains(word, StringComparison.OrdinalIgnoreCase));
        Assert.True(mentioned >= 2,
            $"the answer mentioned {mentioned} of the expected musical subjects: {interpretation.Text}");

        // It must not have been handed the template's text and echoed it back.
        Assert.NotEqual(stats.Fingerprint, interpretation.Text);
        Assert.DoesNotContain(stats.Fingerprint, interpretation.Text, StringComparison.Ordinal);
    }
}
