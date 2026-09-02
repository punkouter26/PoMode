using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.MusicXml;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.MusicXml;

public sealed class RomanNumeralsTests
{
    private static ChordSpan Chord(string root, string quality)
        => new($"{root}{(quality is "min" or "m" ? "m" : "")}", root, quality, 0, 1);

    [Fact]
    public void Triads_map_to_the_expected_numeral_and_raw_fallback()
    {
        Assert.Equal("I", RomanNumerals.Analyze(Chord("C", "maj"), 0));
        Assert.Equal("vi", RomanNumerals.Analyze(Chord("A", "min"), 0));
        Assert.Equal("bVII", RomanNumerals.Analyze(Chord("Bb", "maj"), 0));
        Assert.Equal("ii", RomanNumerals.Analyze(Chord("E", "min"), 2));
        Assert.Equal("N", RomanNumerals.Analyze(new ChordSpan("N", "N", "", 0, 1), 0));
    }

    [Fact]
    public void Pitch_class_parsing_handles_accidentals_and_rejects_junk()
    {
        Assert.True(PitchNames.TryParseRoot("Bb", out var pitchClass));
        Assert.Equal(10, pitchClass);
        Assert.False(PitchNames.TryParseRoot("N", out _));
    }
}
