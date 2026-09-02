using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordViterbiDecoderTests
{
    private static float[] TemplateFor(string symbol)
        => ChordTemplates.All.Single(entry => entry.Chord.Symbol == symbol).Template;

    [Fact]
    public void Viterbi_absorbs_flicker_and_identifies_sustained_runs()
    {
        var framesWithFlicker = Enumerable.Repeat(TemplateFor("C"), 20).ToArray();
        framesWithFlicker[10] = TemplateFor("Am");
        var flickerPath = ChordViterbiDecoder.Decode(framesWithFlicker);
        Assert.All(flickerPath, frame => Assert.Equal("C", frame.Chord.Symbol));

        var twoRuns = Enumerable.Repeat(TemplateFor("C"), 30).Concat(Enumerable.Repeat(TemplateFor("G"), 30)).ToArray();
        var runPath = ChordViterbiDecoder.Decode(twoRuns);
        Assert.Equal("C", runPath[0].Chord.Symbol);
        Assert.Equal("G", runPath[^1].Chord.Symbol);
    }

    [Fact]
    public void Silent_and_empty_frames_decode_to_no_chord()
    {
        var silentFrames = Enumerable.Repeat(new float[12], 10).ToArray();
        var path = ChordViterbiDecoder.Decode(silentFrames);
        Assert.All(path, frame => Assert.Equal("N", frame.Chord.Symbol));

        Assert.Empty(ChordViterbiDecoder.Decode([]));
    }
}
