using PoMode.API.Features.Audio;
using Xunit;

namespace PoMode.Unit.Audio;

public sealed class WavWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-wavwriter-{Guid.NewGuid():N}");

    public WavWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WavWriter_RoundTripsMonoStereoAndClamping()
    {
        var sampleRate = 22050;
        var count = sampleRate;
        var monoSamples = new float[count];
        for (var i = 0; i < count; i++) monoSamples[i] = 0.5f * (float)Math.Sin(2 * Math.PI * 440.0 * i / sampleRate);
        var monoBuffer = new AudioBuffer(monoSamples, sampleRate, 1);
        var monoPath = Path.Combine(_dir, "tone.wav");

        WavWriter.Write(monoPath, monoBuffer);
        var decodedMono = AudioDecoder.Decode(monoPath);
        Assert.Equal(sampleRate, decodedMono.SampleRate);
        Assert.Equal(1, decodedMono.Channels);

        var stereoSamples = new float[count * 2];
        for (var i = 0; i < count; i++)
        {
            stereoSamples[i * 2] = 0.6f * (float)Math.Sin(2 * Math.PI * 440.0 * i / sampleRate);
            stereoSamples[(i * 2) + 1] = 0.3f * (float)Math.Sin(2 * Math.PI * 220.0 * i / sampleRate);
        }
        var stereoBuffer = new AudioBuffer(stereoSamples, sampleRate, 2);
        var stereoPath = Path.Combine(_dir, "stereo.wav");
        WavWriter.Write(stereoPath, stereoBuffer);
        var decodedStereo = AudioDecoder.Decode(stereoPath);
        Assert.Equal(2, decodedStereo.Channels);
    }
}
