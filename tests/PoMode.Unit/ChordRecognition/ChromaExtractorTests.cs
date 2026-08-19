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

    // The triad test below already exercises other pitch classes, so one root is enough here.
    [Theory]
    [InlineData(0)]  // C
    public void A_single_tone_puts_its_energy_in_its_own_pitch_class(int pitchClass)
    {
        var buffer = Chord([48 + pitchClass]);

        var chroma = ChromaExtractor.Compute(buffer).Frames[5];

        var strongest = Array.IndexOf(chroma, chroma.Max());
        Assert.Equal(pitchClass, strongest);
    }

    [Fact]
    public void A_c_major_triad_lights_up_c_e_and_g()
    {
        var buffer = Chord(TestAudio.Triad(0, "maj")); // C E G

        var chroma = ChromaExtractor.Compute(buffer).Frames[5];

        var topThree = chroma
            .Select((value, index) => (value, index))
            .OrderByDescending(pair => pair.value)
            .Take(3)
            .Select(pair => pair.index)
            .Order()
            .ToArray();
        Assert.Equal([0, 4, 7], topThree);
    }

    [Fact]
    public void Chroma_vectors_are_normalised_and_finite()
    {
        var chroma = ChromaExtractor.Compute(Chord(TestAudio.Triad(7, "min"))).Frames[5];

        Assert.All(chroma, value => Assert.True(float.IsFinite(value) && value >= 0));
        var magnitude = Math.Sqrt(chroma.Sum(v => v * v));
        Assert.InRange(magnitude, 0.99, 1.01);
    }

    [Fact]
    public void Silence_yields_a_zero_vector_not_nan()
    {
        var chroma = ChromaExtractor.Frame(new float[4096], 22050);

        Assert.All(chroma, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void Frame_rate_matches_the_hop_size()
    {
        var gram = ChromaExtractor.Compute(Chord(TestAudio.Triad(0, "maj"), seconds: 4.0), windowSize: 4096, hopSize: 2048);

        Assert.InRange(gram.FramesPerSecond, 22050 / 2048.0 - 0.1, 22050 / 2048.0 + 0.1);
        Assert.InRange(gram.Frames.Length, 38, 44); // ~4 s at ~10.8 fps
    }
}
