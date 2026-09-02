using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class ModeDefinitionsTests
{
    [Fact]
    public void Masks_match_the_canonical_spec_table()
    {
        Assert.Equal(0xAB5, ModeDefinitions.Mask(ScaleMode.Ionian));
        Assert.Equal(0x6AD, ModeDefinitions.Mask(ScaleMode.Dorian));
        Assert.Equal(0x4A9, ModeDefinitions.Mask(ScaleMode.MinorPentatonic));
    }

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

    [Fact]
    public void Characteristic_intervals_and_labels_are_accurate()
    {
        Assert.Contains(9, ModeDefinitions.CharacteristicIntervals(ScaleMode.Dorian));
        Assert.Contains(6, ModeDefinitions.CharacteristicIntervals(ScaleMode.Locrian));

        Assert.Equal("b3", PitchNames.IntervalLabel(3));
        Assert.Equal("#4", PitchNames.IntervalLabel(6));
    }
}
