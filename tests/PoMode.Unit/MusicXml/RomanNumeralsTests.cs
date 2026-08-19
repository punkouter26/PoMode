using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.MusicXml;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.MusicXml;

public sealed class RomanNumeralsTests
{
    private static ChordSpan Chord(string root, string quality)
        => new($"{root}{(quality is "min" or "m" ? "m" : "")}", root, quality, 0, 1);

    // One row per numeral shape: uppercase major, lowercase minor, accidental prefix, and a
    // non-C tonic proving the numeral is relative to the tonic rather than to C.
    [Theory]
    [InlineData("C", "maj", 0, "I")]
    [InlineData("A", "min", 0, "vi")]
    [InlineData("Bb", "maj", 0, "bVII")]
    [InlineData("E", "min", 2, "ii")]
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
    [InlineData("Bb", 10)] // flat accidental
    [InlineData("N", -1)]  // reject
    public void Pitch_class_parsing_handles_accidentals_and_rejects_junk(string root, int expected)
    {
        var parsed = PitchNames.TryParseRoot(root, out var pitchClass);
        Assert.Equal(expected >= 0, parsed);
        if (parsed)
        {
            Assert.Equal(expected, pitchClass);
        }
    }
}
