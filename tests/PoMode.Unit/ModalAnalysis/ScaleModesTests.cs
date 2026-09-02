using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class ScaleModesTests
{
    [Fact]
    public void Note_spelling_is_sharp_named_and_defined_for_every_pitch_class()
    {
        Assert.Equal(["F#", "G#", "A#", "B", "C#", "D#", "F"], ScaleModes.NoteNames(6, ScaleMode.Ionian));

        for (var pitchClass = 0; pitchClass < 12; pitchClass++)
        {
            Assert.False(string.IsNullOrWhiteSpace(ScaleModes.NoteName(pitchClass)));
        }
    }

    [Fact]
    public void The_primary_scale_comes_from_the_result_and_is_null_when_no_mode_was_named()
    {
        var result = new ModalResult(1, 0, "C", 1.0, ScaleMode.Dorian, 0.8, 120, false, []);

        Assert.Equal(["C", "D", "D#", "F", "G", "A", "A#"], result.PrimaryScaleNoteNames()!);
        Assert.Null((result with { PrimaryMode = null }).PrimaryScaleNoteNames());
    }

    [Fact]
    public void ModeDegreeOffsets_AreAccurateForAllSevenChurchModes()
    {
        Assert.Equal(0, ScaleModes.ModeDegreeOffset(ScaleMode.Ionian));
        Assert.Equal(2, ScaleModes.ModeDegreeOffset(ScaleMode.Dorian));
        Assert.Equal(4, ScaleModes.ModeDegreeOffset(ScaleMode.Phrygian));
        Assert.Equal(5, ScaleModes.ModeDegreeOffset(ScaleMode.Lydian));
        Assert.Equal(7, ScaleModes.ModeDegreeOffset(ScaleMode.Mixolydian));
        Assert.Equal(9, ScaleModes.ModeDegreeOffset(ScaleMode.Aeolian));
        Assert.Equal(11, ScaleModes.ModeDegreeOffset(ScaleMode.Locrian));
    }
}
