using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class ScaleModesTests
{
    [Fact]
    public void The_primary_scale_and_each_modes_degree_offset_are_right()
    {
        // The primary scale comes from the result and is null when no mode was named
        {
            var result = new ModalResult(1, 0, "C", 1.0, ScaleMode.Dorian, 0.8, 120, false, []);

            Assert.Equal(["C", "D", "D#", "F", "G", "A", "A#"], result.PrimaryScaleNoteNames()!);
            Assert.Null((result with { PrimaryMode = null }).PrimaryScaleNoteNames());
        }

        // ModeDegreeOffsets AreAccurateForAllSevenChurchModes
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
}
