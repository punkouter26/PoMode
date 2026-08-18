using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class ModeDefinitionsTests
{
    // Representative rows only; the structural-invariant tests below catch drift across all modes.
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

    [Fact]
    public void Seven_note_modes_have_seven_notes_and_pentatonics_have_five()
    {
        Assert.Equal(7, ModeDefinitions.Intervals(ScaleMode.Ionian).Count);
        Assert.Equal(7, ModeDefinitions.Intervals(ScaleMode.Locrian).Count);
        Assert.Equal(5, ModeDefinitions.Intervals(ScaleMode.MinorPentatonic).Count);
        Assert.Equal(5, ModeDefinitions.Intervals(ScaleMode.MajorPentatonic).Count);
    }

    [Theory]
    [InlineData(ScaleMode.Dorian, 9)]     // natural 6
    [InlineData(ScaleMode.Lydian, 6)]     // sharp 4
    [InlineData(ScaleMode.Phrygian, 1)]   // flat 2
    [InlineData(ScaleMode.Mixolydian, 10)] // flat 7
    [InlineData(ScaleMode.Locrian, 6)]    // flat 5
    public void Characteristic_intervals_are_in_the_mode(ScaleMode mode, int interval)
    {
        Assert.Contains(interval, ModeDefinitions.CharacteristicIntervals(mode));
        Assert.Contains(interval, ModeDefinitions.Intervals(mode));
    }

    [Theory]
    [InlineData(0, "C")]
    [InlineData(1, "C#")]
    [InlineData(9, "A")]
    [InlineData(11, "B")]
    public void Pitch_names_use_sharps(int pitchClass, string expected)
        => Assert.Equal(expected, PitchNames.Name(pitchClass));

    [Theory]
    [InlineData(0, "1")]
    [InlineData(1, "b2")]
    [InlineData(3, "b3")]
    [InlineData(6, "#4")]
    [InlineData(10, "b7")]
    public void Interval_labels_use_flat_and_sharp_degrees(int semitones, string expected)
        => Assert.Equal(expected, PitchNames.IntervalLabel(semitones));
}
