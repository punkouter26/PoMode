using PoMode.API.Features.Audio;
using Xunit;

namespace PoMode.Unit.Audio;

public sealed class AudioConverterTests
{
    [Fact]
    public void Mono_is_duplicated_to_both_stereo_channels()
    {
        var mono = new AudioBuffer([0.1f, 0.2f, 0.3f], 44100, 1);

        var stereo = AudioConverter.ToStereo(mono);

        Assert.Equal(2, stereo.Channels);
        Assert.Equal([0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.3f], stereo.Samples);
    }

    [Fact]
    public void Multichannel_is_downmixed_then_duplicated_to_stereo()
    {
        // 4 channels, 1 frame: mono average is (1+0-1+0)/4 = 0
        var multi = new AudioBuffer([1f, 0f, -1f, 0f], 44100, 4);

        var stereo = AudioConverter.ToStereo(multi);

        Assert.Equal(2, stereo.Channels);
        Assert.Equal([0f, 0f], stereo.Samples);
    }

    [Fact]
    public void ResampleStereo_preserves_channel_count_and_duration()
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
    }

    // The one converter the stem separator actually calls: mono, low-rate input in a single step.
    [Fact]
    public void ToStereo44100_converts_mono_low_rate_input_in_one_call()
    {
        var frames = 22050;
        var mono = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            mono[i] = 0.4f * (float)Math.Sin(2 * Math.PI * 440.0 * i / 22050);
        }
        var buffer = new AudioBuffer(mono, 22050, 1);

        var result = AudioConverter.ToStereo44100(buffer);

        Assert.Equal(2, result.Channels);
        Assert.Equal(44100, result.SampleRate);
        Assert.InRange(result.DurationSeconds, 0.98, 1.02);
    }
}
