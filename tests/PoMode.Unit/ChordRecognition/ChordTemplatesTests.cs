using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordTemplatesTests
{
    [Fact]
    public void There_are_twelve_distinct_major_and_twelve_distinct_minor_templates()
    {
        Assert.Equal(24, ChordTemplates.All.Count);
        Assert.Equal(12, ChordTemplates.All.Count(entry => entry.Chord.Quality == "maj"));
        Assert.Equal(12, ChordTemplates.All.Count(entry => entry.Chord.Quality == "min"));
        // Rotation must not collide: a duplicate symbol would silently shadow a real chord.
        Assert.Equal(24, ChordTemplates.All.Select(e => e.Chord.Symbol).Distinct().Count());
    }

    [Fact]
    public void C_major_template_has_energy_only_on_c_e_and_g()
    {
        var template = ChordTemplates.All.Single(e => e.Chord.Symbol == "C").Template;

        var nonZero = template.Select((v, i) => (v, i)).Where(p => p.v > 0).Select(p => p.i).Order().ToArray();
        Assert.Equal([0, 4, 7], nonZero);
    }

    [Fact]
    public void Every_template_is_unit_length()
    {
        foreach (var (chord, template) in ChordTemplates.All)
        {
            var magnitude = Math.Sqrt(template.Sum(v => v * v));
            Assert.InRange(magnitude, 0.99, 1.01);
        }
    }

}
