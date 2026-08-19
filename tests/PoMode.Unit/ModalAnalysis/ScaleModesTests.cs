using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class ScaleModesTests
{
    [Fact]
    public void F_sharp_ionian_spells_the_major_scale_with_sharp_names()
    {
        Assert.Equal(["F#", "G#", "A#", "B", "C#", "D#", "F"], ScaleModes.NoteNames(6, ScaleMode.Ionian));
    }

    [Fact]
    public void The_primary_scale_comes_from_the_result_and_is_null_when_no_mode_was_named()
    {
        var result = new ModalResult(1, 0, "C", 1.0, ScaleMode.Dorian, 0.8, 120, false, []);

        Assert.Equal(["C", "D", "D#", "F", "G", "A", "A#"], result.PrimaryScaleNoteNames()!);
        Assert.Null((result with { PrimaryMode = null }).PrimaryScaleNoteNames());
    }
}
