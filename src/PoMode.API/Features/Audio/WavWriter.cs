using System.Buffers.Binary;

namespace PoMode.API.Features.Audio;

/// <summary>
/// Writes an <see cref="AudioBuffer"/> back to a canonical PCM16 WAV file, so pipeline stages that
/// produce audio (stem separation) hand the next stage (pitch tracking, chord recognition) a real file
/// <see cref="AudioDecoder"/> can read back, rather than an in-memory buffer.
/// </summary>
public static class WavWriter
{
    public static void Write(string path, AudioBuffer buffer)
    {
        var dataSize = buffer.Samples.Length * 2; // PCM16
        var blockAlign = (short)(buffer.Channels * 2);

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)buffer.Channels);
        writer.Write(buffer.SampleRate);
        writer.Write(buffer.SampleRate * blockAlign); // byte rate
        writer.Write(blockAlign);
        writer.Write((short)16); // bits per sample
        writer.Write("data"u8);
        writer.Write(dataSize);
        var block = new byte[64 * 1024];
        var offset = 0;
        foreach (var sample in buffer.Samples)
        {
            var clamped = Math.Clamp(sample, -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(offset), (short)(clamped * short.MaxValue));
            offset += 2;
            if (offset == block.Length)
            {
                stream.Write(block, 0, offset);
                offset = 0;
            }
        }
        if (offset > 0)
        {
            stream.Write(block, 0, offset);
        }
    }
}
