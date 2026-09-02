using PoMode.API.Features.Analysis;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Unit.Analysis;

public class AudioFormatValidatorTests
{
    [Fact]
    public void Wav_and_mp3_headers_are_supported()
    {
        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeWav(), out var format));
        Assert.Equal("wav", format);

        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeId3Mp3Header(), out var id3Format));
        Assert.Equal("mp3", id3Format);
        Assert.True(AudioFormatValidator.IsSupported(TestAudio.MakeFrameSyncMp3Header(), out var syncFormat));
        Assert.Equal("mp3", syncFormat);
    }

    [Fact]
    public void Non_audio_and_empty_headers_are_rejected()
    {
        Assert.False(AudioFormatValidator.IsSupported([], out var emptyFormat));
        Assert.Equal("", emptyFormat);

        byte[] aviHeader = [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00, 0x41, 0x56, 0x49, 0x20];
        Assert.False(AudioFormatValidator.IsSupported(aviHeader, out var aviFormat));
        Assert.Equal("", aviFormat);
    }
}
