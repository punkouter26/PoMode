namespace PoMode.API.Features.Audio;

/// <summary>
/// Stereo-aware conversions <see cref="AudioDecoder"/> deliberately doesn't provide (its
/// <see cref="AudioDecoder.Resample"/> is mono-only). HTDemucs hard-requires stereo 44.1 kHz input, so
/// <see cref="OnnxStemSeparator"/> needs both a channel-count fixup and a per-channel resample that
/// re-interleaves afterward.
/// </summary>
public static class AudioConverter
{
    public const int HtDemucsSampleRate = 44100;

    /// <summary>Converts to exactly the shape HTDemucs requires: stereo, 44.1 kHz.</summary>
    public static AudioBuffer ToStereo44100(AudioBuffer buffer)
    {
        var stereo = ToStereo(buffer);
        return ResampleStereo(stereo, HtDemucsSampleRate);
    }

    /// <summary>Mono is duplicated to both channels; >2 channels are downmixed to mono first, then duplicated.</summary>
    public static AudioBuffer ToStereo(AudioBuffer buffer)
    {
        if (buffer.Channels == 2)
        {
            return buffer;
        }

        var mono = buffer.Channels == 1 ? buffer : AudioDecoder.ToMono(buffer);
        var samples = new float[mono.Samples.Length * 2];
        for (var i = 0; i < mono.Samples.Length; i++)
        {
            samples[i * 2] = mono.Samples[i];
            samples[(i * 2) + 1] = mono.Samples[i];
        }
        return new AudioBuffer(samples, mono.SampleRate, 2);
    }

    /// <summary>De-interleaves, resamples each channel independently via <see cref="AudioDecoder.Resample"/>, re-interleaves.</summary>
    public static AudioBuffer ResampleStereo(AudioBuffer buffer, int targetSampleRate)
    {
        if (buffer.Channels != 2)
        {
            throw new ArgumentException("ResampleStereo expects stereo input; call ToStereo first.", nameof(buffer));
        }
        if (buffer.SampleRate == targetSampleRate)
        {
            return buffer;
        }

        var frames = buffer.Samples.Length / 2;
        var left = new float[frames];
        var right = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            left[i] = buffer.Samples[i * 2];
            right[i] = buffer.Samples[(i * 2) + 1];
        }

        var resampledLeft = AudioDecoder.Resample(new AudioBuffer(left, buffer.SampleRate, 1), targetSampleRate);
        var resampledRight = AudioDecoder.Resample(new AudioBuffer(right, buffer.SampleRate, 1), targetSampleRate);

        var outFrames = Math.Min(resampledLeft.Samples.Length, resampledRight.Samples.Length);
        var interleaved = new float[outFrames * 2];
        for (var i = 0; i < outFrames; i++)
        {
            interleaved[i * 2] = resampledLeft.Samples[i];
            interleaved[(i * 2) + 1] = resampledRight.Samples[i];
        }
        return new AudioBuffer(interleaved, targetSampleRate, 2);
    }
}
