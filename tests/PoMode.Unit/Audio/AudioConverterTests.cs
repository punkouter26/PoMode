using PoMode.API.Features.Audio;
using Xunit;

namespace PoMode.Unit.Audio;

public sealed class AudioConverterTests
{
    [Fact]
    public void Mono_and_multichannel_downmix_and_duplicate_to_stereo()
    {
        var mono = new AudioBuffer([0.1f, 0.2f, 0.3f], 44100, 1);
        var stereo = AudioConverter.ToStereo(mono);
        Assert.Equal(2, stereo.Channels);
        Assert.Equal([0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.3f], stereo.Samples);

        var multi = new AudioBuffer([1f, 0f, -1f, 0f], 44100, 4);
        var multiStereo = AudioConverter.ToStereo(multi);
        Assert.Equal(2, multiStereo.Channels);
        Assert.Equal([0f, 0f], multiStereo.Samples);
    }

    [Fact]
    public void ResampleStereo_and_ToStereo44100_preserve_duration()
    {
        var frames = 22050;
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            samples[i * 2] = 0.5f * (float)Math.Sin(2 * Math.PI * 440.0 * i / 22050);
            samples[(i * 2) + 1] = 0.25f * (float)Math.Sin(2 * Math.PI * 220.0 * i / 22050);
        }
        var buffer = new AudioBuffer(samples, 22050, 2);
        var resampled = AudioConverter.ResampleStereo(buffer, 44100);
        Assert.Equal(2, resampled.Channels);
        Assert.Equal(44100, resampled.SampleRate);
        Assert.InRange(resampled.DurationSeconds, 0.98, 1.02);

        var mono = new float[frames];
        var monoBuffer = new AudioBuffer(mono, 22050, 1);
        var converted = AudioConverter.ToStereo44100(monoBuffer);
        Assert.Equal(2, converted.Channels);
        Assert.Equal(44100, converted.SampleRate);
    }
}
