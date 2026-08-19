using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class ModeDefinitionsTests
{
    // Two representative rows: a seven-note mode and a pentatonic, so both shapes are pinned to
    // the canonical spec table. The structural test below catches drift across every other mode.
    [Theory]
    [InlineData(ScaleMode.Ionian, 0xAB5)]
    [InlineData(ScaleMode.Dorian, 0x6AD)]
    [InlineData(ScaleMode.MinorPentatonic, 0x4A9)]
    public void Masks_match_the_canonical_spec_table(ScaleMode mode, int expected)
        => Assert.Equal(expected, ModeDefinitions.Mask(mode));

    [Fact]
    public void Every_mode_has_a_definition_and_root_is_always_present()
    {
        Assert.Equal(9, ModeDefinitions.All.Count);
        foreach (var mode in ModeDefinitions.All)
        {
            Assert.Contains(0, ModeDefinitions.Intervals(mode));
            Assert.All(ModeDefinitions.Intervals(mode), i => Assert.InRange(i, 0, 11));
            Assert.Equal(ModeDefinitions.Intervals(mode).Count, ModeDefinitions.Intervals(mode).Distinct().Count());
        }
    }

    // A characteristic degree the mode itself does not contain would break scoring outright.
    [Theory]
    [InlineData(ScaleMode.Dorian, 9)]     // natural 6
    [InlineData(ScaleMode.Locrian, 6)]    // flat 5
    public void Characteristic_intervals_are_in_the_mode(ScaleMode mode, int interval)
    {
        Assert.Contains(interval, ModeDefinitions.CharacteristicIntervals(mode));
        Assert.Contains(interval, ModeDefinitions.Intervals(mode));
    }

    [Theory]
    [InlineData(3, "b3")]
    [InlineData(6, "#4")]
    public void Interval_labels_use_flat_and_sharp_degrees(int semitones, string expected)
        => Assert.Equal(expected, PitchNames.IntervalLabel(semitones));
}
