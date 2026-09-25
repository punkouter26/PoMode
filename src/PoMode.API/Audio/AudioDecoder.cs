using NAudio.Wave;
using NLayer.NAudioSupport;
using PoMode.API.Features.Analysis;

namespace PoMode.API.Audio;

/// <summary>Decodes uploads to normalised float PCM. Format is sniffed from content, never the extension.</summary>
public static class AudioDecoder
{
    /// <summary>15 minutes — generous for a song, ~318 MB decoded worst case at stereo 44.1 kHz.</summary>
    public const double MaxDurationSecondsDefault = 900;

    public static AudioBuffer Decode(string path, double maxDurationSeconds = MaxDurationSecondsDefault)
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

        var totalSeconds = reader.TotalTime.TotalSeconds;
        if (totalSeconds > maxDurationSeconds)
        {
            throw new InvalidDataException($"Audio is {totalSeconds:0} s long; the limit is {maxDurationSeconds:0} s.");
        }

        var provider = reader.ToSampleProvider();
        var format = provider.WaveFormat;

        // Capacity hint only — TotalTime can be inexact for compressed formats, so the read loop
        // below still owns the true length. The ceiling guards against corrupt headers that
        // declare absurd rates: 1 << 27 floats (~512 MB) covers the duration cap at 48 kHz
        // stereo with headroom, and a genuine longer file just grows past the hint normally.
        var estimatedSamples = (long)Math.Ceiling(totalSeconds * format.SampleRate * format.Channels);
        var buffer = new List<float>(capacity: (int)Math.Clamp(estimatedSamples, 1 << 20, 1 << 27));
        var chunk = new float[format.SampleRate * format.Channels];
        int count;
        while ((count = provider.Read(chunk.AsSpan())) > 0)
        {
            buffer.AddRange(chunk.AsSpan(0, count));
        }

        return new AudioBuffer([.. buffer], format.SampleRate, format.Channels);
    }

    /// <summary>
    /// Duration only, without decoding a single sample — the container's own header is enough. Used to
    /// bound client-supplied note times (Tier 2) where a full <see cref="Decode"/> would mean hundreds
    /// of megabytes of work on an endpoint. Returns null for anything unreadable or unsupported.
    /// </summary>
    public static double? TryReadDurationSeconds(string path)
    {
        try
        {
            var header = new byte[12];
            using (var probe = File.OpenRead(path))
            {
                var read = probe.Read(header);
                if (!AudioFormatValidator.IsSupported(header.AsSpan(0, read), out _))
                {
                    return null;
                }
            }
            using var reader = OpenReader(path, header);
            return reader.TotalTime.TotalSeconds;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// A min/max envelope in <paramref name="columns"/> equal slices, streamed rather than decoded
    /// whole: a waveform sketch of a five-minute mp3 should not cost the hundred megabytes of floats
    /// <see cref="Decode"/> would hold. Channels are averaged. Null for anything unreadable.
    /// </summary>
    public static (float[] Peaks, double DurationSec)? TryReadPeaks(string path, int columns)
    {
        try
        {
            var header = new byte[12];
            using (var probe = File.OpenRead(path))
            {
                var read = probe.Read(header);
                if (!AudioFormatValidator.IsSupported(header.AsSpan(0, read), out _))
                {
                    return null;
                }
            }
            using var reader = OpenReader(path, header);
            var provider = reader.ToSampleProvider();
            var channels = Math.Max(provider.WaveFormat.Channels, 1);
            // TotalTime is exact for WAV and close for mp3; a frame past the estimate lands in the
            // last column rather than being dropped.
            var estimatedFrames = Math.Max((long)(reader.TotalTime.TotalSeconds * provider.WaveFormat.SampleRate), 1);
            var framesPerColumn = Math.Max(estimatedFrames / columns, 1);

            var peaks = new float[columns * 2];
            var chunk = new float[provider.WaveFormat.SampleRate * channels];
            long frame = 0;
            int count;
            while ((count = provider.Read(chunk.AsSpan())) > 0)
            {
                for (var i = 0; i + channels <= count; i += channels)
                {
                    var sum = 0f;
                    for (var channel = 0; channel < channels; channel++)
                    {
                        sum += chunk[i + channel];
                    }
                    var value = sum / channels;
                    var column = (int)Math.Min(frame / framesPerColumn, columns - 1);
                    peaks[column * 2] = Math.Min(peaks[column * 2], value);
                    peaks[(column * 2) + 1] = Math.Max(peaks[(column * 2) + 1], value);
                    frame++;
                }
            }
            return (peaks, frame / (double)provider.WaveFormat.SampleRate);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException)
        {
            return null;
        }
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

    /// <summary>
    /// Windowed-sinc resampling with the anti-aliasing <see cref="Resample"/> skips. Linear
    /// interpolation is fine for the classic DSP stages, which only look well below the new Nyquist,
    /// but a neural front end's top mel bands sit right at it: downsampling 44.1 kHz to 16 kHz by
    /// interpolation folds cymbals and sibilance back into exactly the bands the model reads, as
    /// energy it never saw in training. Mono only, like <see cref="Resample"/>.
    /// </summary>
    public static AudioBuffer ResampleBandLimited(AudioBuffer buffer, int targetSampleRate)
    {
        if (buffer.Channels != 1)
        {
            throw new ArgumentException("Resample expects mono input; call ToMono first.", nameof(buffer));
        }
        if (buffer.SampleRate == targetSampleRate || buffer.Samples.Length == 0)
        {
            return buffer with { SampleRate = targetSampleRate };
        }

        const int ZeroCrossings = 16;
        var input = buffer.Samples;
        var ratio = (double)targetSampleRate / buffer.SampleRate;
        // Cutoff as a fraction of the input rate: the lower Nyquist, pulled in slightly so the
        // transition band finishes before it rather than straddling it.
        var cutoff = 0.5 * Math.Min(1.0, ratio) * 0.95;
        var halfWidth = ZeroCrossings / (2.0 * cutoff); // in input samples
        var output = new float[(int)(input.Length * ratio)];

        // The kernel tabulated at 1/256-sample resolution: evaluating sin and cos per tap would
        // cost seconds on a full song, and the table's error is far below 16-bit audio's.
        const int Resolution = 256;
        var kernel = new float[(int)Math.Ceiling(2 * halfWidth * Resolution) + 2];
        for (var t = 0; t < kernel.Length; t++)
        {
            var x = (t / (double)Resolution) - halfWidth;
            if (Math.Abs(x) > halfWidth)
            {
                continue;
            }
            var arg = 2.0 * cutoff * x;
            var sinc = Math.Abs(arg) < 1e-9 ? 1.0 : Math.Sin(Math.PI * arg) / (Math.PI * arg);
            var window = 0.5 + (0.5 * Math.Cos(Math.PI * x / halfWidth)); // Hann
            kernel[t] = (float)(2.0 * cutoff * sinc * window);
        }

        Parallel.For(0, output.Length, i =>
        {
            var centre = i / ratio;
            var first = Math.Max(0, (int)Math.Ceiling(centre - halfWidth));
            var last = Math.Min(input.Length - 1, (int)Math.Floor(centre + halfWidth));
            var sum = 0f;
            for (var j = first; j <= last; j++)
            {
                sum += input[j] * kernel[(int)(((j - centre + halfWidth) * Resolution) + 0.5)];
            }
            output[i] = sum;
        });
        return new AudioBuffer(output, targetSampleRate, 1);
    }
}
