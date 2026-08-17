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
    }

    [Fact]
    public void Beat_sync_respects_the_grid_phase()
    {
        // First beat at 0.25 s: boundaries at 0.25, 0.75, 1.25… The change at 1.3 s falls in beat
        // [1.25, 1.75) which G wins (frames 13-16 of 17 are G: G majority 4 of 5).
        var spans = ChordSegmenter.Segment(Frames(("C", 13), ("G", 12)), Fps, Grid(firstBeatSec: 0.25), medianWindow: 1);

        Assert.Equal(2, spans.Count);
        Assert.Equal(1.25, spans[0].EndSec, precision: 9);
        Assert.Equal(1.25, spans[1].StartSec, precision: 9);
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

    [Fact]
    public void Beat_sync_drops_no_chord_beats_and_covers_the_track_edges()
    {
        var spans = ChordSegmenter.Segment(Frames(("N", 10), ("C", 15)), Fps, Grid(), medianWindow: 1);

        var span = Assert.Single(spans);
        Assert.Equal("C", span.Symbol);
        Assert.Equal(1.0, span.StartSec, precision: 9);  // N owns beats [0,0.5) and [0.5,1.0)
        Assert.Equal(2.5, span.EndSec, precision: 9);    // final partial beat reaches the track end
    }
}
