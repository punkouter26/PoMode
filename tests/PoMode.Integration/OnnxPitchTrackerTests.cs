using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
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

    [Fact]
    public async Task A_440_hz_tone_is_detected_as_A4()
    {
        var registry = Registry();
        if (!registry.IsDownloaded(ModelCatalog.BasicPitch))
        {
            Console.WriteLine(
                "SKIPPED: Basic Pitch model not downloaded to this machine (models/nmp.onnx absent) — " +
                "nothing to verify without network access to fetch it.");
            Assert.True(true);
            return;
        }

        var tracker = new OnnxPitchTracker(registry, NullLogger<OnnxPitchTracker>.Instance);
        var inputPath = Path.Combine(_dir, "tone.wav");
        File.WriteAllBytes(inputPath, TestAudio.MakeTone(seconds: 5.0, frequencyHz: 440.0));
        var context = new StageContext("job", _dir, inputPath);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var notes = await tracker.TrackAsync(context, CancellationToken.None);
        stopwatch.Stop();

        Console.WriteLine(
            $"Detected {notes.Count} note(s) in {stopwatch.ElapsedMilliseconds} ms: " +
            string.Join(", ", notes.Select(n => $"midi={n.MidiPitch} start={n.StartSec:F2}s dur={n.DurationSec:F2}s vel={n.Velocity}")));

        Assert.NotEmpty(notes);
        Assert.Contains(notes, n => n.MidiPitch >= 68 && n.MidiPitch <= 70);
    }
}
