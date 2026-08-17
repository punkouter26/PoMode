using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordMatcherTests
{
    private static float[] Chroma(params int[] pitchClasses)
    {
        var chroma = new float[12];
        foreach (var pitchClass in pitchClasses)
        {
            chroma[pitchClass] = 1f;
        }
        var magnitude = (float)Math.Sqrt(chroma.Sum(v => v * v));
        return magnitude == 0 ? chroma : [.. chroma.Select(v => v / magnitude)];
    }

    [Theory]
    [InlineData(new[] { 0, 4, 7 }, "C")]
    [InlineData(new[] { 9, 0, 4 }, "Am")]
    [InlineData(new[] { 7, 11, 2 }, "G")]
    [InlineData(new[] { 2, 5, 9 }, "Dm")]
    [InlineData(new[] { 6, 10, 1 }, "F#")]
    public void A_clean_triad_matches_its_own_chord(int[] pitchClasses, string expected)
    {
        var (chord, score) = ChordMatcher.Match(Chroma(pitchClasses));

        Assert.Equal(expected, chord.Symbol);
        Assert.True(score > 0.9, $"score was {score}");
    }

    [Fact]
    public void A_triad_with_one_extra_note_still_matches()
    {
        // C E G plus a passing D — should still be C, just less confidently.
        var (chord, score) = ChordMatcher.Match(Chroma(0, 4, 7, 2));

        Assert.Equal("C", chord.Symbol);
        Assert.True(score > 0.55);
    }

    [Fact]
    public void Noise_across_all_twelve_classes_is_no_chord()
    {
        var (chord, score) = ChordMatcher.Match(Chroma(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11));

        Assert.Equal("N", chord.Symbol);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Silence_is_no_chord()
    {
        var (chord, _) = ChordMatcher.Match(new float[12]);

        Assert.Equal("N", chord.Symbol);
    }

    [Fact]
    public void Major_and_minor_are_distinguished_by_the_third()
    {
        Assert.Equal("C", ChordMatcher.Match(Chroma(0, 4, 7)).Chord.Symbol);
        Assert.Equal("Cm", ChordMatcher.Match(Chroma(0, 3, 7)).Chord.Symbol);
    }
}
