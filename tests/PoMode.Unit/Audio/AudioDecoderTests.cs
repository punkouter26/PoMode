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
    public void Decodes_and_resamples_audio_buffers_accurately()
    {
        var path = WriteTemp("tone.wav", TestAudio.MakeTone(seconds: 1.0, frequencyHz: 440, sampleRate: 22050, amplitude: 0.5));
        var buffer = AudioDecoder.Decode(path);

        Assert.Equal(22050, buffer.SampleRate);
        Assert.Equal(1, buffer.Channels);
        Assert.InRange(buffer.DurationSeconds, 0.98, 1.02);
        Assert.All(buffer.Samples, s => Assert.InRange(s, -1.0f, 1.0f));

        var resampled = AudioDecoder.Resample(buffer, 11025);
        Assert.Equal(11025, resampled.SampleRate);
        Assert.InRange(resampled.DurationSeconds, 0.98, 1.02);
    }

    [Fact]
    public void Unsupported_content_and_oversized_audio_are_rejected()
    {
        var junkPath = WriteTemp("junk.wav", [0x25, 0x50, 0x44, 0x46, 0, 0, 0, 0, 0, 0, 0, 0]);
        var ex = Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode(junkPath));
        Assert.Contains("audio", ex.Message, StringComparison.OrdinalIgnoreCase);

        var longPath = WriteTemp("long.wav", TestAudio.MakeTone(seconds: 10.0, frequencyHz: 440, sampleRate: 22050));
        var exLimit = Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode(longPath, maxDurationSeconds: 5.0));
        Assert.Contains("limit", exLimit.Message, StringComparison.OrdinalIgnoreCase);
    }
}
