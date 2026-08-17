namespace PoMode.TestCommon;

/// <summary>Generates minimal valid audio fixtures for tests. Linked (not referenced) into each test project.</summary>
public static class TestAudio
{
    public static byte[] MakeWav(double seconds = 0.1, int sampleRate = 8000)
    {
        var samples = (int)(seconds * sampleRate);
        var dataSize = samples * 2; // PCM16 mono
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);          // PCM
        writer.Write((short)1);          // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);    // byte rate
        writer.Write((short)2);          // block align
        writer.Write((short)16);         // bits per sample
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]); // silence
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] MakeId3Mp3Header() => [.. "ID3"u8, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    public static byte[] MakeFrameSyncMp3Header() => [0xFF, 0xFB, 0x90, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>A mono sine tone as a valid PCM16 WAV — real signal for decode and tempo tests.</summary>
    public static byte[] MakeTone(double seconds, double frequencyHz, int sampleRate = 22050, double amplitude = 0.5)
    {
        var count = (int)(seconds * sampleRate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataSize = count * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var i = 0; i < count; i++)
        {
            var value = amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate);
            writer.Write((short)(value * short.MaxValue));
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>A two-tone stereo mix (different frequency per channel) — synthetic input for stem separation tests.</summary>
    public static byte[] MakeTwoToneStereo(double seconds, double frequencyHzLeft, double frequencyHzRight, int sampleRate = 44100, double amplitude = 0.3)
    {
        var count = (int)(seconds * sampleRate);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataSize = count * 4; // stereo PCM16 = 4 bytes/frame
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2); // stereo
        writer.Write(sampleRate);
        writer.Write(sampleRate * 4); // byte rate
        writer.Write((short)4);       // block align
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var i = 0; i < count; i++)
        {
            var left = amplitude * Math.Sin(2 * Math.PI * frequencyHzLeft * i / sampleRate);
            var right = amplitude * Math.Sin(2 * Math.PI * frequencyHzRight * i / sampleRate);
            writer.Write((short)(left * short.MaxValue));
            writer.Write((short)(right * short.MaxValue));
        }
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Clicks at a fixed BPM — ground truth for the tempo estimator.</summary>
    public static byte[] MakeClickTrack(double seconds, double bpm, int sampleRate = 22050)
    {
        var count = (int)(seconds * sampleRate);
        var samples = new short[count];
        var samplesPerBeat = 60.0 / bpm * sampleRate;
        for (var beat = 0; beat * samplesPerBeat < count; beat++)
        {
            var start = (int)(beat * samplesPerBeat);
            for (var i = 0; i < 200 && start + i < count; i++)
            {
                // Short decaying burst = a sharp onset the envelope can find.
                var envelope = 1.0 - (i / 200.0);
                samples[start + i] = (short)(envelope * 0.8 * short.MaxValue * Math.Sin(2 * Math.PI * 1000 * i / sampleRate));
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataSize = count * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }
        writer.Flush();
        return stream.ToArray();
    }
}
