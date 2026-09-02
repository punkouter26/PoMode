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
            for (var i = 0; i < count; i++) frames.Add((Chord(symbol), symbol == "N" ? 0.0 : 0.9));
        }
        return frames;
    }

    private static PoMode.API.Features.Audio.BeatGrid Grid(double bpm = 120.0, double firstBeatSec = 0.0)
        => new(bpm, firstBeatSec, Confidence: 1.0);

    [Fact]
    public void Segment_groups_consecutive_runs_into_contiguous_spans()
    {
        var spans = ChordSegmenter.Segment(Frames(("C", 30), ("G", 30)), Fps);
        Assert.Equal(2, spans.Count);
        Assert.Equal(["C", "G"], spans.Select(s => s.Symbol).ToArray());
        Assert.Equal(spans[0].EndSec, spans[1].StartSec);
    }

    [Fact]
    public void Beat_sync_snaps_boundaries_and_absorbs_subbeat_flicker()
    {
        var spans = ChordSegmenter.Segment(Frames(("C", 12), ("G", 13)), Fps, Grid(), medianWindow: 1);
        Assert.Equal(2, spans.Count);
        Assert.Equal(("C", 0.0, 1.0), (spans[0].Symbol, spans[0].StartSec, spans[0].EndSec));
        Assert.Equal(("G", 1.0, 2.5), (spans[1].Symbol, spans[1].StartSec, spans[1].EndSec));

        var flickerSpans = ChordSegmenter.Segment(Frames(("C", 11), ("G", 2), ("C", 12)), Fps, Grid(), medianWindow: 1);
        Assert.Equal("C", Assert.Single(flickerSpans).Symbol);
    }
}
