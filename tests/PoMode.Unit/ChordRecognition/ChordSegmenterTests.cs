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
    public void Two_runs_become_two_contiguous_spans()
    {
        var spans = ChordSegmenter.Segment(Frames(("C", 30), ("G", 30)), Fps);

        Assert.Equal(2, spans.Count);
        Assert.Equal(["C", "G"], spans.Select(s => s.Symbol).ToArray());
        Assert.Equal(spans[0].EndSec, spans[1].StartSec);
    }

    // --- Beat-synchronous overload (§13.6 fix b) ---

    /// <summary>120 BPM ⇒ 0.5 s beats ⇒ 5 frames per beat at the test's 10 fps.</summary>
    private static PoMode.API.Features.Audio.BeatGrid Grid(double bpm = 120.0, double firstBeatSec = 0.0)
        => new(bpm, firstBeatSec, Confidence: 1.0);

    [Fact]
    public void Beat_sync_snaps_a_mid_beat_change_to_the_nearest_beat_by_majority()
    {
        // The change lands at 1.2 s, inside beat [1.0, 1.5): frames C C G G G → G wins the beat,
        // so the boundary snaps back to 1.0 exactly.
        var spans = ChordSegmenter.Segment(Frames(("C", 12), ("G", 13)), Fps, Grid(), medianWindow: 1);

        Assert.Equal(2, spans.Count);
        Assert.Equal(("C", 0.0, 1.0), (spans[0].Symbol, spans[0].StartSec, spans[0].EndSec));
        Assert.Equal(("G", 1.0, 2.5), (spans[1].Symbol, spans[1].StartSec, spans[1].EndSec));
    }

    [Fact]
    public void Beat_sync_absorbs_flicker_shorter_than_a_beat_without_a_duration_floor()
    {
        // Two G frames inside a C beat can never outvote it — no minimum-duration rule involved.
        var spans = ChordSegmenter.Segment(Frames(("C", 11), ("G", 2), ("C", 12)), Fps, Grid(), medianWindow: 1);

        var span = Assert.Single(spans);
        Assert.Equal("C", span.Symbol);
        // The same majority vote drops "no chord" beats and still covers the track's final
        // partial beat, so silence at the head does not shift the timeline.
        var withSilence = ChordSegmenter.Segment(Frames(("N", 10), ("C", 15)), Fps, Grid(), medianWindow: 1);
        var only = Assert.Single(withSilence);
        Assert.Equal(("C", 1.0, 2.5), (only.Symbol, only.StartSec, only.EndSec));
    }

    [Fact]
    public void A_low_confidence_grid_falls_back_to_the_duration_floor_path()
    {
        var grid = new PoMode.API.Features.Audio.BeatGrid(120.0, 0.0, Confidence: 0.0);

        var withGrid = ChordSegmenter.Segment(Frames(("C", 30), ("G", 30)), Fps, grid);
        var without = ChordSegmenter.Segment(Frames(("C", 30), ("G", 30)), Fps);

        Assert.Equal(without.Count, withGrid.Count);
        Assert.Equal(without[0].EndSec, withGrid[0].EndSec);
    }

}
