namespace PoMode.API.Features.Analysis;

/// <summary>Magic-byte sniffing for uploads; extension is never trusted.</summary>
public static class AudioFormatValidator
{
    public const long MaxBytes = 100L * 1024 * 1024;

    public static bool IsSupported(ReadOnlySpan<byte> header, out string format)
    {
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8))
        {
            format = "wav";
            return true;
        }

        if (header.Length >= 3 && header[..3].SequenceEqual("ID3"u8))
        {
            format = "mp3";
            return true;
        }

        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
        {
            format = "mp3";
            return true;
        }

        format = "";
        return false;
    }
}
