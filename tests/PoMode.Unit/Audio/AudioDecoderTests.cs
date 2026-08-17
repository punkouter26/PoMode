using PoMode.API.Features.Audio;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Unit.Audio;

public sealed class AudioDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-audio-{Guid.NewGuid():N}");

    public AudioDecoderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteTemp(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Decodes_a_wav_to_the_expected_length_and_rate()
    {
        var path = WriteTemp("tone.wav", TestAudio.MakeTone(seconds: 1.0, frequencyHz: 440, sampleRate: 22050));

        var buffer = AudioDecoder.Decode(path);

        Assert.Equal(22050, buffer.SampleRate);
        Assert.Equal(1, buffer.Channels);
        Assert.InRange(buffer.DurationSeconds, 0.98, 1.02);
    }

    [Fact]
    public void Decoded_samples_are_normalised_floats()
    {
        var path = WriteTemp("tone.wav", TestAudio.MakeTone(1.0, 440, amplitude: 0.5));

        var buffer = AudioDecoder.Decode(path);

        Assert.All(buffer.Samples, s => Assert.InRange(s, -1.0f, 1.0f));
        Assert.InRange(buffer.Samples.Max(), 0.4f, 0.6f); // amplitude survives
    }

    [Fact]
    public void Resampling_halves_the_sample_count_when_halving_the_rate()
    {
        var buffer = AudioDecoder.Decode(WriteTemp("tone.wav", TestAudio.MakeTone(1.0, 440, sampleRate: 44100)));

        var resampled = AudioDecoder.Resample(buffer, 22050);

        Assert.Equal(22050, resampled.SampleRate);
        Assert.InRange(resampled.Samples.Length, 22000, 22100);
        Assert.InRange(resampled.DurationSeconds, 0.98, 1.02);
    }

    [Fact]
    public void Mono_conversion_averages_channels()
    {
        var stereo = new AudioBuffer([1.0f, -1.0f, 0.5f, -0.5f], 8000, 2);

        var mono = AudioDecoder.ToMono(stereo);

        Assert.Equal(1, mono.Channels);
        Assert.Equal([0.0f, 0.0f], mono.Samples);
    }

    [Fact]
    public void Unsupported_content_throws_a_clear_error()
    {
        var path = WriteTemp("junk.wav", [0x25, 0x50, 0x44, 0x46, 0, 0, 0, 0, 0, 0, 0, 0]);

        var ex = Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode(path));
        Assert.Contains("audio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The brief's tests only exercise the WAV path. This decodes a real MP3 (the NLayer-backed
    /// path) using the user's own local file, which is git-ignored and never touched or committed.
    /// If the file isn't present (e.g. CI, another machine), the test skips cleanly with a logged
    /// reason instead of failing.
    /// </summary>
    [Fact]
    public void Decodes_a_real_mp3_via_the_nlayer_path()
    {
        var source = Path.Combine(FindRepoRoot(), "2017_LonelyHill2.mp3");
        if (!File.Exists(source))
        {
            Console.WriteLine($"SKIPPED: real-MP3 fixture not found at '{source}'; nothing to verify on this machine.");
            return;
        }

        var path = Path.Combine(_dir, "real.mp3");
        File.Copy(source, path);

        var buffer = AudioDecoder.Decode(path);

        Assert.True(buffer.SampleRate >= 8000, $"Expected a plausible sample rate, got {buffer.SampleRate}.");
        Assert.True(buffer.Channels >= 1, $"Expected at least one channel, got {buffer.Channels}.");
        Assert.True(buffer.DurationSeconds > 10, $"Expected duration > 10s, got {buffer.DurationSeconds:F2}s.");
        Console.WriteLine($"Decoded '{source}': {buffer.SampleRate} Hz, {buffer.Channels} ch, {buffer.DurationSeconds:F2}s.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoMode.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("PoMode.slnx not found above test bin dir.");
    }
}
