using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordTemplatesTests
{
    [Fact]
    public void There_are_twelve_major_and_twelve_minor_templates()
    {
        Assert.Equal(24, ChordTemplates.All.Count);
        Assert.Equal(12, ChordTemplates.All.Count(entry => entry.Chord.Quality == "maj"));
        Assert.Equal(12, ChordTemplates.All.Count(entry => entry.Chord.Quality == "min"));
    }

    [Fact]
    public void C_major_template_has_energy_only_on_c_e_and_g()
    {
        var template = ChordTemplates.All.Single(e => e.Chord.Symbol == "C").Template;

        var nonZero = template.Select((v, i) => (v, i)).Where(p => p.v > 0).Select(p => p.i).Order().ToArray();
        Assert.Equal([0, 4, 7], nonZero);
    }

    [Fact]
    public void A_minor_template_has_energy_only_on_a_c_and_e()
    {
        var template = ChordTemplates.All.Single(e => e.Chord.Symbol == "Am").Template;

        var nonZero = template.Select((v, i) => (v, i)).Where(p => p.v > 0).Select(p => p.i).Order().ToArray();
        Assert.Equal([0, 4, 9], nonZero);
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

    [Fact]
    public void Symbols_and_roots_are_well_formed()
    {
        Assert.Contains(ChordTemplates.All, e => e.Chord.Symbol == "F#" && e.Chord.Root == "F#" && e.Chord.Quality == "maj");
        Assert.Contains(ChordTemplates.All, e => e.Chord.Symbol == "C#m" && e.Chord.Root == "C#" && e.Chord.Quality == "min");
        Assert.Equal(24, ChordTemplates.All.Select(e => e.Chord.Symbol).Distinct().Count());
    }

    [Fact]
    public void No_chord_is_its_own_candidate()
        => Assert.Equal("N", ChordTemplates.NoChord.Symbol);
}
