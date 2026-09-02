using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordMatcherTests
{
    private static float[] Chroma(params int[] pitchClasses)
    {
        var chroma = new float[12];
        foreach (var pitchClass in pitchClasses) chroma[pitchClass] = 1f;
        var magnitude = (float)Math.Sqrt(chroma.Sum(v => v * v));
        return magnitude == 0 ? chroma : [.. chroma.Select(v => v / magnitude)];
    }

    [Fact]
    public void Clean_and_noisy_triads_match_accurately()
    {
        var (cMajor, cScore) = ChordMatcher.Match(Chroma(0, 4, 7));
        Assert.Equal("C", cMajor.Symbol);
        Assert.True(cScore > 0.9);

        var (aMinor, aScore) = ChordMatcher.Match(Chroma(9, 0, 4));
        Assert.Equal("Am", aMinor.Symbol);
        Assert.True(aScore > 0.9);

        var (cPassing, passingScore) = ChordMatcher.Match(Chroma(0, 4, 7, 2));
        Assert.Equal("C", cPassing.Symbol);
        Assert.True(passingScore > 0.55);
    }

    [Fact]
    public void Flat_noise_and_silence_yield_no_chord()
    {
        var (noiseChord, noiseScore) = ChordMatcher.Match(Chroma(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11));
        Assert.Equal("N", noiseChord.Symbol);
        Assert.Equal(0.0, noiseScore);

        var (silenceChord, _) = ChordMatcher.Match(new float[12]);
        Assert.Equal("N", silenceChord.Symbol);
    }
}
