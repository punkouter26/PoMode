using System.Buffers.Binary;
using System.Text;
using PoMode.API.Features.ModalMelodies;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public sealed class ModalWavSynthesizerTests
{
    [Fact]
    public void Synthesize_ProducesValidRiffPcmWavHeaderAndAudioBytes()
    {
        var generator = new ModalMelodyGenerator();
        var request = new ModalMelodyRequest(
            TonicPitchClass: 0,
            Mode: ScaleMode.Dorian,
            ProgressionId: "pop-axis",
            Bpm: 120.0,
            Style: MelodyStyle.Lyrical,
            Seed: 42);

        var generated = generator.Generate(request);
        var duration = generated.Chords.Count > 0 ? generated.Chords[^1].EndSec : 8.0;

        var wavBytes = ModalWavSynthesizer.Synthesize(
            melodyNotes: generated.MelodyNotes,
            chords: generated.Chords,
            totalDurationSec: duration);

        Assert.NotNull(wavBytes);
        Assert.True(wavBytes.Length > 44, "WAV bytes must contain header plus audio payload.");

        var riffHeader = Encoding.ASCII.GetString(wavBytes, 0, 4);
        Assert.Equal("RIFF", riffHeader);

        var waveFormat = Encoding.ASCII.GetString(wavBytes, 8, 4);
        Assert.Equal("WAVE", waveFormat);

        var fmtSubchunk = Encoding.ASCII.GetString(wavBytes, 12, 4);
        Assert.Equal("fmt ", fmtSubchunk);

        var subchunk1Size = BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(16, 4));
        Assert.Equal(16, subchunk1Size);

        var audioFormat = BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(20, 2));
        Assert.Equal(1, audioFormat);

        var numChannels = BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(22, 2));
        Assert.Equal(2, numChannels);

        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(24, 4));
        Assert.Equal(44100, sampleRate);

        var bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(34, 2));
        Assert.Equal(16, bitsPerSample);

        var dataSubchunk = Encoding.ASCII.GetString(wavBytes, 36, 4);
        Assert.Equal("data", dataSubchunk);

        var dataSize = BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(40, 4));
        Assert.Equal(wavBytes.Length - 44, dataSize);
    }

    [Fact]
    public void Synthesize_EmptyMelody_ProducesValidHeaderAndSilence()
    {
        var wavBytes = ModalWavSynthesizer.Synthesize([], [], 2.0);
        Assert.NotNull(wavBytes);
        Assert.True(wavBytes.Length > 44);

        var riffHeader = Encoding.ASCII.GetString(wavBytes, 0, 4);
        Assert.Equal("RIFF", riffHeader);
    }

    [Fact]
    public void Synthesize_CalculatesExpectedByteSizeForDuration()
    {
        const double duration = 4.0;
        var wavBytes = ModalWavSynthesizer.Synthesize([], [], duration);
        var actualDuration = Math.Max(duration + 1.0, 4.0);
        var expectedSamples = (int)(actualDuration * 44100);
        var expectedPayloadBytes = expectedSamples * 2 * 2; // 2 channels * 16-bit (2 bytes)
        var expectedTotalBytes = 44 + expectedPayloadBytes;

        Assert.Equal(expectedTotalBytes, wavBytes.Length);
    }
}
