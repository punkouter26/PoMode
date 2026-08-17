using PoMode.API.Features.MusicXml;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.MusicXml;

public sealed class RomanNumeralsTests
{
    private static ChordSpan Chord(string root, string quality)
        => new($"{root}{(quality is "min" or "m" ? "m" : "")}", root, quality, 0, 1);

    [Theory]
    [InlineData("C", "maj", 0, "I")]
    [InlineData("G", "maj", 0, "V")]
    [InlineData("A", "min", 0, "vi")]
    [InlineData("D", "min", 0, "ii")]
    [InlineData("Bb", "maj", 0, "bVII")]
    [InlineData("F#", "min", 0, "#iv")]
    [InlineData("D", "maj", 2, "I")]  // D tonic: D major is I
    [InlineData("E", "min", 2, "ii")] // D tonic: E minor is ii
    public void Triads_map_to_the_expected_numeral(string root, string quality, int tonic, string expected)
    {
        Assert.Equal(expected, RomanNumerals.Analyze(Chord(root, quality), tonic));
    }

    [Fact]
    public void No_chord_falls_back_to_the_raw_symbol()
    {
        var chord = new ChordSpan("N", "N", "", 0, 1);
        Assert.Equal("N", RomanNumerals.Analyze(chord, 0));
    }

    [Theory]
    [InlineData("C", 0)]
    [InlineData("C#", 1)]
    [InlineData("Db", 1)]
    [InlineData("B", 11)]
    [InlineData("Bb", 10)]
    [InlineData("N", -1)]
    [InlineData("", -1)]
    [InlineData("H", -1)]
    public void Pitch_class_parsing_handles_accidentals_and_rejects_junk(string root, int expected)
    {
        Assert.Equal(expected, RomanNumerals.ParsePitchClass(root));
    }
}
