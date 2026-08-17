using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// End-to-end proof that the real Basic Pitch ONNX model produces musically correct output. Skips
/// cleanly (no assertion, no failure) when the model has not been downloaded to this machine yet —
/// CI without network access must still pass the fast suite.
/// </summary>
[Trait("Category", "Slow")]
public sealed class OnnxPitchTrackerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-onnx-{Guid.NewGuid():N}");
    private readonly ModelRegistry _registry = Registry();

    public OnnxPitchTrackerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ModelRegistry Registry()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        return new ModelRegistry(
            new ConfigurationBuilder().Build(),
            provider.GetRequiredService<IHttpClientFactory>(),
            NullLogger<ModelRegistry>.Instance);
    }

    private bool ModelAvailable() => _registry.IsDownloaded(ModelCatalog.BasicPitch);

    private async Task<IReadOnlyList<NoteEvent>> TrackToneAsync(double seconds, double frequencyHz)
    {
        var tracker = new OnnxPitchTracker(_registry, NullLogger<OnnxPitchTracker>.Instance);
        var inputPath = Path.Combine(_dir, "tone.wav");
        File.WriteAllBytes(inputPath, TestAudio.MakeTone(seconds, frequencyHz));
        var context = new StageContext("job", _dir, inputPath);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var notes = await tracker.TrackAsync(context, CancellationToken.None);
        stopwatch.Stop();

        Console.WriteLine(
            $"Detected {notes.Count} note(s) in {stopwatch.ElapsedMilliseconds} ms: " +
            string.Join(", ", notes.Select(n => $"midi={n.MidiPitch} start={n.StartSec:F2}s dur={n.DurationSec:F2}s vel={n.Velocity}")));

        return notes;
    }

    [Fact]
    public async Task A_sustained_tone_is_one_note_starting_near_zero()
    {
        // 5 s of continuous A4 from t=0. Must be ONE note, starting near 0, lasting most of the clip.
        // Guards both failure modes: a missed/displaced first onset, and a phantom re-onset at a window seam.
        if (!ModelAvailable())
        {
            Console.WriteLine(
                "SKIPPED: Basic Pitch model not downloaded to this machine (models/nmp.onnx absent) — " +
                "nothing to verify without network access to fetch it.");
            Assert.True(true);
            return;
        }

        var notes = await TrackToneAsync(seconds: 5.0, frequencyHz: 440.0);

        var note = Assert.Single(notes);
        Assert.Equal(69, note.MidiPitch);
        Assert.True(note.StartSec < 0.30, $"onset was {note.StartSec:0.000}s, expected < 0.30s");
        Assert.True(note.DurationSec > 4.0, $"duration was {note.DurationSec:0.000}s, expected > 4.0s");
    }
}
