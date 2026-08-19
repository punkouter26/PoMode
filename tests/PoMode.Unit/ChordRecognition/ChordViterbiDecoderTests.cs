using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordViterbiDecoderTests
{
    private static float[] TemplateFor(string symbol)
        => ChordTemplates.All.Single(entry => entry.Chord.Symbol == symbol).Template;

    [Fact]
    public void A_single_outlier_frame_is_absorbed_by_the_surrounding_chord()
    {
        var frames = Enumerable.Repeat(TemplateFor("C"), 20).ToArray();
        frames[10] = TemplateFor("Am"); // one frame of flicker mid-chord

        var path = ChordViterbiDecoder.Decode(frames);

        Assert.All(path, frame => Assert.Equal("C", frame.Chord.Symbol));
    }

    [Fact]
    public void A_sustained_chord_change_produces_exactly_two_runs()
    {
        var frames = Enumerable.Repeat(TemplateFor("C"), 30)
            .Concat(Enumerable.Repeat(TemplateFor("G"), 30))
            .ToArray();

        var path = ChordViterbiDecoder.Decode(frames);

        Assert.Equal("C", path[0].Chord.Symbol);
        Assert.Equal("G", path[^1].Chord.Symbol);
        var runs = 1;
        for (var i = 1; i < path.Count; i++)
        {
            if (path[i].Chord.Symbol != path[i - 1].Chord.Symbol)
            {
                runs++;
            }
        }
        Assert.Equal(2, runs);
    }

    [Fact]
    public void Silent_frames_decode_to_no_chord()
    {
        var frames = Enumerable.Repeat(new float[12], 10).ToArray();

        var path = ChordViterbiDecoder.Decode(frames);

        Assert.All(path, frame => Assert.Equal("N", frame.Chord.Symbol));
    }

    [Fact]
    public void An_empty_frame_sequence_decodes_to_an_empty_path()
    {
        Assert.Empty(ChordViterbiDecoder.Decode([]));
    }
}
