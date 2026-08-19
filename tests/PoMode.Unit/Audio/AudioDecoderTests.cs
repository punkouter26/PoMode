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
    public void Decodes_a_wav_to_the_expected_length_rate_and_normalised_samples()
    {
        var path = WriteTemp("tone.wav", TestAudio.MakeTone(seconds: 1.0, frequencyHz: 440, sampleRate: 22050, amplitude: 0.5));

        var buffer = AudioDecoder.Decode(path);

        Assert.Equal(22050, buffer.SampleRate);
        Assert.Equal(1, buffer.Channels);
        Assert.InRange(buffer.DurationSeconds, 0.98, 1.02);
        Assert.All(buffer.Samples, s => Assert.InRange(s, -1.0f, 1.0f));
        Assert.InRange(buffer.Samples.Max(), 0.4f, 0.6f); // amplitude survives normalisation
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

    // Channel downmixing is covered by AudioConverterTests' multichannel case.
    [Fact]
    public void Unsupported_content_throws_a_clear_error()
    {
        var path = WriteTemp("junk.wav", [0x25, 0x50, 0x44, 0x46, 0, 0, 0, 0, 0, 0, 0, 0]);

        var ex = Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode(path));
        Assert.Contains("audio", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Audio_longer_than_the_limit_is_rejected_before_decoding()
    {
        var path = WriteTemp("long.wav", TestAudio.MakeTone(seconds: 3.0, frequencyHz: 440, sampleRate: 8000));

        var ex = Assert.Throws<InvalidDataException>(() => AudioDecoder.Decode(path, maxDurationSeconds: 1.0));

        Assert.Contains("limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

}
