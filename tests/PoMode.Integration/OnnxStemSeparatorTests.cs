using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PoMode.API.Features.Audio;
using PoMode.API.Features.StemSeparation;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

/// <summary>
/// End-to-end proof that the real HTDemucs ONNX model runs and produces decodable, correctly-sized
/// stems. Skips cleanly (no assertion, no failure) when the model has not been downloaded to this
/// machine yet — CI without network access must still pass the fast suite. Does NOT assert separation
/// quality (not deterministically testable per the Task 8 brief); it asserts structural correctness:
/// both stems exist, decode, match the input's duration, and sum back to approximate the original mix
/// (instrumental is constructed as mix - vocals, so this is mostly a construction sanity check, not a
/// quality claim about the model).
/// </summary>
[Trait("Category", "Slow")]
public sealed class OnnxStemSeparatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-stem-{Guid.NewGuid():N}");
    private readonly ModelRegistry _registry = Registry();

    public OnnxStemSeparatorTests() => Directory.CreateDirectory(_dir);

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

    private bool ModelAvailable() => _registry.IsDownloaded(ModelCatalog.HtDemucs);

    [Fact]
    public async Task Separates_a_ten_second_mix_into_decodable_stems_that_reconstruct_the_original()
    {
        if (!ModelAvailable())
        {
            Console.WriteLine(
                "SKIPPED: HTDemucs model not downloaded to this machine (models/htdemucs_fp16weights.onnx absent) — " +
                "nothing to verify without network access to fetch it.");
            Assert.True(true);
            return;
        }

        var separator = new OnnxStemSeparator(_registry, NullLogger<OnnxStemSeparator>.Instance);
        var inputPath = Path.Combine(_dir, "mix.wav");
        File.WriteAllBytes(inputPath, TestAudio.MakeTwoToneStereo(seconds: 10.0, frequencyHzLeft: 440, frequencyHzRight: 220));
        var context = new StageContext("job", _dir, inputPath);

        var stopwatch = Stopwatch.StartNew();
        await separator.SeparateAsync(context, CancellationToken.None);
        stopwatch.Stop();
        Console.WriteLine($"10s separation took {stopwatch.Elapsed.TotalSeconds:F1}s wall (RTF={stopwatch.Elapsed.TotalSeconds / 10.0:F2}).");

        var vocalsPath = Path.Combine(_dir, "vocals.wav");
        var instrumentalPath = Path.Combine(_dir, "instrumental.wav");
        Assert.True(File.Exists(vocalsPath), "vocals.wav was not written");
        Assert.True(File.Exists(instrumentalPath), "instrumental.wav was not written");

        var original = AudioConverter.ToStereo44100(AudioDecoder.Decode(inputPath));
        var vocals = AudioDecoder.Decode(vocalsPath);
        var instrumental = AudioDecoder.Decode(instrumentalPath);

        Assert.InRange(vocals.DurationSeconds, original.DurationSeconds * 0.95, original.DurationSeconds * 1.05);
        Assert.InRange(instrumental.DurationSeconds, original.DurationSeconds * 0.95, original.DurationSeconds * 1.05);

        var count = new[] { original.Samples.Length, vocals.Samples.Length, instrumental.Samples.Length }.Min();
        Assert.True(count > 0, "no samples decoded to compare");

        var sumAbsError = 0.0;
        for (var i = 0; i < count; i++)
        {
            var reconstructed = vocals.Samples[i] + instrumental.Samples[i];
            sumAbsError += Math.Abs(reconstructed - original.Samples[i]);
        }
        var meanAbsError = sumAbsError / count;
        Console.WriteLine($"Reconstruction mean absolute error: {meanAbsError:F5} over {count} samples.");
        Assert.True(meanAbsError < 0.05, $"mean absolute reconstruction error was {meanAbsError:F5}, expected < 0.05");
    }
}
