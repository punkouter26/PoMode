using PoMode.API.Features.Audio;
using PoMode.API.Features.ChordRecognition;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public sealed class ChromaExtractorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pomode-chroma-{Guid.NewGuid():N}");

    public ChromaExtractorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AudioBuffer Chord(int[] pitches, double seconds = 2.0)
    {
        var path = Path.Combine(_dir, $"chord-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, TestAudio.MakeChord(seconds, pitches));
        return AudioDecoder.Decode(path);
    }

    [Fact]
    public void Single_tone_and_triad_exhibit_correct_chroma_energy()
    {
        var single = ChromaExtractor.Compute(Chord([48])).Frames[5];
        Assert.Equal(0, Array.IndexOf(single, single.Max()));

        var triad = ChromaExtractor.Compute(Chord(TestAudio.Triad(0, "maj"))).Frames[5];
        var topThree = triad
            .Select((value, index) => (value, index))
            .OrderByDescending(pair => pair.value)
            .Take(3)
            .Select(pair => pair.index)
            .Order()
            .ToArray();
        Assert.Equal([0, 4, 7], topThree);
    }

    [Fact]
    public void Chroma_vectors_are_normalised_finite_and_handle_silence()
    {
        var chroma = ChromaExtractor.Compute(Chord(TestAudio.Triad(7, "min"))).Frames[5];
        Assert.All(chroma, value => Assert.True(float.IsFinite(value) && value >= 0));
        var magnitude = Math.Sqrt(chroma.Sum(v => v * v));
        Assert.InRange(magnitude, 0.99, 1.01);

        var silence = ChromaExtractor.Compute(new AudioBuffer(new float[22050 * 2], 22050, 1)).Frames[5];
        Assert.All(silence, value => Assert.Equal(0f, value));
    }
}
