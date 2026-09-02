using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordTemplatesTests
{
    [Fact]
    public void ChordTemplates_structure_and_unit_normalization()
    {
        Assert.Equal(24, ChordTemplates.All.Count);
        Assert.Equal(12, ChordTemplates.All.Count(entry => entry.Chord.Quality == "maj"));
        Assert.Equal(12, ChordTemplates.All.Count(entry => entry.Chord.Quality == "min"));
        Assert.Equal(24, ChordTemplates.All.Select(e => e.Chord.Symbol).Distinct().Count());

        var cMajor = ChordTemplates.All.Single(e => e.Chord.Symbol == "C").Template;
        var nonZero = cMajor.Select((v, i) => (v, i)).Where(p => p.v > 0).Select(p => p.i).Order().ToArray();
        Assert.Equal([0, 4, 7], nonZero);

        foreach (var (_, template) in ChordTemplates.All)
        {
            var magnitude = Math.Sqrt(template.Sum(v => v * v));
            Assert.InRange(magnitude, 0.99, 1.01);
        }
    }
}
