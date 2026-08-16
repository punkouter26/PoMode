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
}
