using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Features.BeatTracking;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// The two newer local models on audio with a known answer: RMVPE on a voice-like line and Beat This!
/// on a groove with a known bar grid. Like <see cref="OnnxPitchTrackerTests"/>, each passes without
/// asserting when its model is not on this machine — the models download at runtime and are never
/// committed. The model cache is found by globbing the API's bin folder, so running the app once is
/// enough to arm these.
/// </summary>
[Trait("Category", "Slow")]
public sealed class NeuralExecutorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-neural-{Guid.NewGuid():N}");

    public NeuralExecutorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Rmvpe_follows_a_sung_line_with_vibrato_note_for_note()
    {
        var registry = Registry(ModelCatalog.Rmvpe);
        if (!registry.IsDownloaded(ModelCatalog.Rmvpe))
        {
            Console.WriteLine("SKIPPED: RMVPE not downloaded to this machine.");
            return;
        }

        (int Midi, double StartSec, double DurationSec)[] truth =
            [(62, 0.3, 0.7), (65, 1.1, 0.5), (69, 1.7, 0.9), (67, 2.7, 0.4), (64, 3.2, 0.4), (62, 3.7, 1.0)];
        File.WriteAllBytes(Path.Combine(_dir, "vocals.wav"), TestAudio.MakeSungLine(5.0, truth, noiseLevel: 0.01));
        var context = new StageContext("job", _dir, Path.Combine(_dir, "vocals.wav"));

        var notes = await new RmvpePitchTracker(registry, NullLogger<RmvpePitchTracker>.Instance)
            .TrackAsync(context, CancellationToken.None);

        Console.WriteLine(string.Join(", ", notes.Select(n => $"{n.MidiPitch}@{n.StartSec:0.00}+{n.DurationSec:0.00}")));
        // One note per sung note, despite ±40 cents of vibrato on every one of them.
        Assert.Equal(truth.Length, notes.Count);
        for (var i = 0; i < truth.Length; i++)
        {
            Assert.Equal(truth[i].Midi, notes[i].MidiPitch);
            Assert.InRange(notes[i].StartSec, truth[i].StartSec - 0.05, truth[i].StartSec + 0.08);
        }
    }

    [Fact]
    public async Task Beat_this_finds_the_tempo_and_where_each_bar_starts()
    {
        var registry = Registry(ModelCatalog.BeatThis);
        var tracker = new BeatThisBeatTracker(registry, NullLogger<BeatThisBeatTracker>.Instance);
        if (!await tracker.IsAvailableAsync(CancellationToken.None))
        {
            Console.WriteLine("SKIPPED: Beat This! not downloaded to this machine.");
            return;
        }

        // Bar one starts 0.73 s in, so a tracker that assumes the grid starts at t=0 is wrong.
        var (wav, _, downbeats) = TestAudio.MakeDrumLoop(bars: 8, bpm: 104, leadInSec: 0.73);
        var path = Path.Combine(_dir, "groove.wav");
        File.WriteAllBytes(path, wav);

        var result = await tracker.TrackBeatsAsync(new StageContext("job", _dir, path), CancellationToken.None);

        Console.WriteLine($"{result.Grid.Bpm} BPM, confidence {result.Grid.Confidence}, downbeats "
            + string.Join(", ", result.Grid.Downbeats?.Select(d => d.ToString("0.00")) ?? []));
        Assert.Equal(nameof(BeatThisBeatTracker), result.Grid.Tracker);
        Assert.InRange(result.Grid.Bpm, 104 * 0.98, 104 * 1.02);
        Assert.NotNull(result.Grid.Downbeats);
        // Every true bar start is found within 70 ms (the standard beat-tracking tolerance).
        foreach (var truth in downbeats)
        {
            Assert.Contains(result.Grid.Downbeats!, heard => Math.Abs(heard - truth) <= 0.07);
        }
        Assert.All(result.TempoMap.Measures, measure => Assert.InRange(measure.Bpm, 100, 108));
    }

    /// <summary>Points at the app's own model cache (same discovery as the accuracy report).</summary>
    private static ModelRegistry Registry(ModelDescriptor wanted)
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        var apiBin = Path.Combine(TestPaths.RepoRoot(), "src", "PoMode.API", "bin");
        var appModels = Directory.Exists(apiBin)
            ? Directory.GetDirectories(apiBin, "models", SearchOption.AllDirectories)
                .FirstOrDefault(dir => File.Exists(Path.Combine(dir, wanted.FileName)))
            : null;
        var config = new ConfigurationBuilder();
        if (appModels is not null)
        {
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Models:RootPath"] = appModels });
        }
        return new ModelRegistry(
            config.Build(), provider.GetRequiredService<IHttpClientFactory>(), NullLogger<ModelRegistry>.Instance);
    }
}
