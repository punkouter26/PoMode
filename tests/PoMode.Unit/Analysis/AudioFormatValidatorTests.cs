using Xunit;
using PoMode.API.Features.Analysis;
using PoMode.TestCommon;

namespace PoMode.Unit.Analysis;

public class AudioFormatValidatorTests
{
    [Fact]
    public void Wav_header_is_supported()
    {
        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeWav(), out var format));
        Assert.Equal("wav", format);
    }

    [Fact]
    public void Id3_mp3_header_is_supported()
    {
        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeId3Mp3Header(), out var format));
        Assert.Equal("mp3", format);
    }

    [Fact]
    public void Frame_sync_mp3_header_is_supported()
    {
        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeFrameSyncMp3Header(), out var format));
        Assert.Equal("mp3", format);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x00 })]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A, 0x25, 0x0A, 0x0A })] // %PDF-1.4
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x41, 0x56, 0x49, 0x20 })] // RIFF....AVI (not WAVE)
    public void Other_content_is_rejected(byte[] header)
    {
        Assert.False(AudioFormatValidator.IsSupported(header, out var format));
        Assert.Equal("", format);
    }

    [Fact]
    public void Max_size_is_100_mb()
        => Assert.Equal(104_857_600L, AudioFormatValidator.MaxBytes);
}
