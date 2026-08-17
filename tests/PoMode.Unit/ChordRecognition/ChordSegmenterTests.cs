using PoMode.API.Features.ChordRecognition;
using Xunit;

namespace PoMode.Unit.ChordRecognition;

public class ChordSegmenterTests
{
    private const double Fps = 10.0;

    private static ChordCandidate Chord(string symbol) =>
        ChordTemplates.All.FirstOrDefault(e => e.Chord.Symbol == symbol).Chord ?? ChordTemplates.NoChord;

    private static List<(ChordCandidate, double)> Frames(params (string Symbol, int Count)[] runs)
    {
        var frames = new List<(ChordCandidate, double)>();
        foreach (var (symbol, count) in runs)
        {
            for (var i = 0; i < count; i++)
            {
                frames.Add((Chord(symbol), symbol == "N" ? 0.0 : 0.9));
            }
        }
        return frames;
    }

    [Fact]
    public void A_steady_run_becomes_one_span()
    {
        var spans = ChordSegmenter.Segment(Frames(("C", 40)), Fps);

        var span = Assert.Single(spans);
        Assert.Equal("C", span.Symbol);
        Assert.Equal(0.0, span.StartSec);
        Assert.InRange(span.EndSec, 3.9, 4.1);
    }

    [Fact]
    public void Two_runs_become_two_contiguous_spans()
    {
        var spans = ChordSegmenter.Segment(Frames(("C", 30), ("G", 30)), Fps);

        Assert.Equal(2, spans.Count);
        Assert.Equal(["C", "G"], spans.Select(s => s.Symbol).ToArray());
        Assert.Equal(spans[0].EndSec, spans[1].StartSec);
    }

    [Fact]
    public void A_single_flickering_frame_is_smoothed_away()
    {
        var spans = ChordSegmenter.Segment(Frames(("C", 20), ("G", 1), ("C", 20)), Fps);

        var span = Assert.Single(spans);
        Assert.Equal("C", span.Symbol);
    }

    [Fact]
    public void Spans_shorter_than_the_minimum_are_absorbed()
    {
        // 2 frames of G = 0.2 s, under the 0.5 s floor.
        var spans = ChordSegmenter.Segment(Frames(("C", 30), ("G", 2), ("C", 30)), Fps, medianWindow: 1);

        Assert.Single(spans);
        Assert.Equal("C", spans[0].Symbol);
    }

    [Fact]
    public void No_chord_regions_are_dropped_from_the_output()
    {
        var spans = ChordSegmenter.Segment(Frames(("N", 30), ("C", 30)), Fps);

        var span = Assert.Single(spans);
        Assert.Equal("C", span.Symbol);
        Assert.InRange(span.StartSec, 2.9, 3.1);
    }

    [Fact]
    public void Root_and_quality_survive_into_the_span()
    {
        var span = ChordSegmenter.Segment(Frames(("Am", 40)), Fps).Single();

        Assert.Equal("Am", span.Symbol);
        Assert.Equal("A", span.Root);
        Assert.Equal("min", span.Quality);
    }

    [Fact]
    public void Empty_input_yields_no_spans()
        => Assert.Empty(ChordSegmenter.Segment([], Fps));
}
