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

    /// <summary>Both MP3 detection branches — an ID3 tag and a bare frame sync — in one place.</summary>
    [Fact]
    public void Both_mp3_header_forms_are_supported()
    {
        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeId3Mp3Header(), out var id3Format));
        Assert.Equal("mp3", id3Format);
        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeFrameSyncMp3Header(), out var syncFormat));
        Assert.Equal("mp3", syncFormat);
    }

    [Theory]
    [InlineData(new byte[0])] // nothing to sniff
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x41, 0x56, 0x49, 0x20 })] // RIFF....AVI (not WAVE)
    public void Other_content_is_rejected(byte[] header)
    {
        Assert.False(AudioFormatValidator.IsSupported(header, out var format));
        Assert.Equal("", format);
    }
}
