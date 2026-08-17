using PoMode.API.Features.Audio;
using Xunit;

namespace PoMode.Unit.Audio;

public sealed class WavWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-wavwriter-{Guid.NewGuid():N}");

    public WavWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Round_trips_a_mono_tone_through_the_decoder()
    {
        var sampleRate = 22050;
        var seconds = 1.0;
        var frequencyHz = 440.0;
        var amplitude = 0.5f;
        var count = (int)(seconds * sampleRate);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate);
        }
        var buffer = new AudioBuffer(samples, sampleRate, 1);
        var path = Path.Combine(_dir, "tone.wav");

        WavWriter.Write(path, buffer);
        var decoded = AudioDecoder.Decode(path);

        Assert.Equal(sampleRate, decoded.SampleRate);
        Assert.Equal(1, decoded.Channels);
        Assert.InRange(decoded.DurationSeconds, seconds * 0.98, seconds * 1.02);
        var expectedPeak = amplitude;
        var actualPeak = decoded.Samples.Select(Math.Abs).Max();
        Assert.InRange(actualPeak, expectedPeak * 0.99, expectedPeak * 1.01);
    }

    [Fact]
    public void Round_trips_a_stereo_buffer_through_the_decoder()
    {
        var sampleRate = 44100;
        var seconds = 0.5;
        var count = (int)(seconds * sampleRate);
        var samples = new float[count * 2];
        for (var i = 0; i < count; i++)
        {
            samples[i * 2] = 0.6f * (float)Math.Sin(2 * Math.PI * 440.0 * i / sampleRate);
            samples[(i * 2) + 1] = 0.3f * (float)Math.Sin(2 * Math.PI * 220.0 * i / sampleRate);
        }
        var buffer = new AudioBuffer(samples, sampleRate, 2);
        var path = Path.Combine(_dir, "stereo.wav");

        WavWriter.Write(path, buffer);
        var decoded = AudioDecoder.Decode(path);

        Assert.Equal(sampleRate, decoded.SampleRate);
        Assert.Equal(2, decoded.Channels);
        Assert.InRange(decoded.DurationSeconds, seconds * 0.98, seconds * 1.02);
    }

    [Fact]
    public void Clamps_out_of_range_samples_instead_of_overflowing()
    {
        var buffer = new AudioBuffer([1.5f, -1.5f, 0f], 8000, 1);
        var path = Path.Combine(_dir, "clamped.wav");

        WavWriter.Write(path, buffer);
        var decoded = AudioDecoder.Decode(path);

        Assert.All(decoded.Samples, s => Assert.InRange(s, -1.0f, 1.0f));
    }

    [Fact]
    public void Writes_a_file_that_exists_with_nonzero_length()
    {
        var buffer = new AudioBuffer([0.1f, -0.1f, 0.2f, -0.2f], 8000, 1);
        var path = Path.Combine(_dir, "nonempty.wav");

        WavWriter.Write(path, buffer);

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 44); // header + data
    }
}
