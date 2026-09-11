using PoMode.API.Features.ModalMelodies;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

/// <summary>
/// The chord track a hum take is analyzed against. A singer loops the progression as many times as
/// they like before pressing stop, so the one thing this has to get right is that every pass carries
/// the chords that were actually sounding during it — the harmony is the take's reference frame, and
/// a take whose second pass has no chords under it is scored as a mode over silence.
/// </summary>
public class HumTakeSeederTests
{
    /// <summary>Two bars of Dm–Gm at 2 seconds each: a 4-second loop.</summary>
    private static IReadOnlyList<ChordSpan> Loop() =>
    [
        new("Dm", "D", "min", 0.0, 2.0),
        new("Gm", "G", "min", 2.0, 4.0),
    ];

    [Fact]
    public void A_take_spanning_several_passes_carries_the_chords_of_every_pass()
    {
        var tiled = HumTakeSeeder.TileOverTake(Loop(), loopSeconds: 4.0, takeSeconds: 12.0);

        Assert.Equal(6, tiled.Count);
        Assert.Equal(
            ["Dm", "Gm", "Dm", "Gm", "Dm", "Gm"],
            tiled.Select(chord => chord.Symbol));
        Assert.Equal(
            [0.0, 2.0, 4.0, 6.0, 8.0, 10.0],
            tiled.Select(chord => chord.StartSec));

        // No gaps and no overlaps: the mode engine windows on these spans, so a seam would either
        // drop notes out of the analysis or score them twice.
        for (var i = 1; i < tiled.Count; i++)
        {
            Assert.Equal(tiled[i - 1].EndSec, tiled[i].StartSec, precision: 6);
        }
    }

    [Fact]
    public void A_take_stopped_mid_bar_ends_with_the_audio_rather_than_with_the_loop()
    {
        // Stop pressed 1 second into the second pass's first bar.
        var tiled = HumTakeSeeder.TileOverTake(Loop(), loopSeconds: 4.0, takeSeconds: 5.0);

        Assert.Equal(3, tiled.Count);
        Assert.Equal("Dm", tiled[^1].Symbol);
        Assert.Equal(4.0, tiled[^1].StartSec, precision: 6);
        // Clipped to the recording, not run out to 6.0s where the bar would have ended: nobody sang
        // that second, so no window may claim it.
        Assert.Equal(5.0, tiled[^1].EndSec, precision: 6);
    }

    [Fact]
    public void A_sliver_left_over_at_the_end_is_dropped_rather_than_scored()
    {
        // Stop pressed a tenth of a second into a new bar — too little to say anything about a mode.
        var tiled = HumTakeSeeder.TileOverTake(Loop(), loopSeconds: 4.0, takeSeconds: 4.1);

        Assert.Equal(2, tiled.Count);
        Assert.Equal(4.0, tiled[^1].EndSec, precision: 6);
    }

    [Fact]
    public void A_take_shorter_than_one_pass_keeps_only_the_bars_it_reached()
    {
        var tiled = HumTakeSeeder.TileOverTake(Loop(), loopSeconds: 4.0, takeSeconds: 2.0);

        Assert.Single(tiled);
        Assert.Equal("Dm", tiled[0].Symbol);
        Assert.Equal(2.0, tiled[0].EndSec, precision: 6);
    }

    [Fact]
    public void Tiling_terminates_on_a_take_far_longer_than_its_loop()
    {
        // The page caps a take at five minutes; the tiling loop must bound itself regardless.
        var tiled = HumTakeSeeder.TileOverTake(Loop(), loopSeconds: 4.0, takeSeconds: 100_000.0);

        Assert.NotEmpty(tiled);
        Assert.True(tiled.Count <= 4096);
    }
}
