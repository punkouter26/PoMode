using NAudio.Wave;
using NLayer.NAudioSupport;
using PoMode.API.Features.Analysis;

namespace PoMode.API.Features.Audio;

/// <summary>Decodes uploads to normalised float PCM. Format is sniffed from content, never the extension.</summary>
public static class AudioDecoder
{
    public static AudioBuffer Decode(string path)
    {
        var header = new byte[12];
        using (var probe = File.OpenRead(path))
        {
            var read = probe.Read(header);
            if (!AudioFormatValidator.IsSupported(header.AsSpan(0, read), out _))
            {
                throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a supported audio file (wav or mp3).");
            }
        }

        using var reader = OpenReader(path, header);
        var provider = reader.ToSampleProvider();
        var format = provider.WaveFormat;

        var buffer = new List<float>(capacity: 1 << 20);
        var chunk = new float[format.SampleRate * format.Channels];
        int count;
        while ((count = provider.Read(chunk.AsSpan())) > 0)
        {
            buffer.AddRange(chunk.AsSpan(0, count));
        }

        return new AudioBuffer([.. buffer], format.SampleRate, format.Channels);
    }

    private static WaveStream OpenReader(string path, ReadOnlySpan<byte> header)
        => header[..4].SequenceEqual("RIFF"u8)
            ? new WaveFileReader(path)
            : new Mp3FileReaderBase(path, wave => new Mp3FrameDecompressor(wave));

    public static AudioBuffer ToMono(AudioBuffer buffer)
    {
        if (buffer.Channels <= 1)
        {
            return buffer;
        }

        var frames = buffer.Samples.Length / buffer.Channels;
        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < buffer.Channels; channel++)
            {
                sum += buffer.Samples[(frame * buffer.Channels) + channel];
            }
            mono[frame] = sum / buffer.Channels;
        }
        return new AudioBuffer(mono, buffer.SampleRate, 1);
    }

    public static AudioBuffer Resample(AudioBuffer buffer, int targetSampleRate)
    {
        if (buffer.Channels != 1)
        {
            throw new ArgumentException("Resample expects mono input; call ToMono first.", nameof(buffer));
        }
        if (buffer.SampleRate == targetSampleRate || buffer.Samples.Length == 0)
        {
            return buffer with { SampleRate = targetSampleRate };
        }

        var ratio = (double)targetSampleRate / buffer.SampleRate;
        var length = (int)(buffer.Samples.Length * ratio);
        var output = new float[length];
        for (var i = 0; i < length; i++)
        {
            var source = i / ratio;
            var index = (int)source;
            var fraction = (float)(source - index);
            var a = buffer.Samples[Math.Min(index, buffer.Samples.Length - 1)];
            var b = buffer.Samples[Math.Min(index + 1, buffer.Samples.Length - 1)];
            output[i] = a + ((b - a) * fraction);
        }
        return new AudioBuffer(output, targetSampleRate, 1);
    }
}
