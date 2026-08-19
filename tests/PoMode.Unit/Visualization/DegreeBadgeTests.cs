using PoMode.API.Features.Visualization;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Visualization;

/// <summary>
/// The HUD renders whatever the payload says, so the badge/alternative derivation is tested here
/// rather than in the browser. Same discipline as the note roles: no music theory in the client.
/// </summary>
public class DegreeBadgeTests
{
    private static ModalResult ResultWith(
        IReadOnlyList<ModalMatch> matches,
        int[] sungIntervals,
        bool insufficient = false,
        ScaleMode? primaryMode = ScaleMode.Ionian,
        int tonicPitchClass = 0)
    {
        var vocalMask = sungIntervals.Aggregate(0, (mask, interval) => mask | (1 << interval));
        var window = new ModalWindow(
            Index: 0,
            StartSec: 0.0,
            EndSec: 4.0,
            ChordSymbol: "Dm",
            MeasureNumber: 3,
            VocalMask: vocalMask,
            SungIntervals: sungIntervals,
            InsufficientEvidence: insufficient,
            Matches: matches);

        return new ModalResult(1, tonicPitchClass, "C", 0.8, primaryMode, 0.75, 120.0, false, [window]);
    }

    private static VisualWindow Build(ModalResult result)
        => VisualizationBuilder.Build([], [], result).Windows.Single();

    [Fact]
    public void Characteristic_degrees_are_flagged_separately_from_plain_in_mode()
    {
        var window = Build(ResultWith([new ModalMatch(ScaleMode.Dorian, 0.9, [], [])], [0]));

        // Dorian's characteristic degrees are the natural 6 (9) over the minor 3 (3).
        Assert.Equal([3, 9], window.Degrees.Where(b => b.Characteristic).Select(b => b.Interval).Order().ToArray());
        Assert.All(window.Degrees.Where(b => b.Characteristic), badge => Assert.True(badge.InMode));
    }

    [Fact]
    public void An_insufficient_window_does_not_borrow_the_primary_mode_for_its_badges()
    {
        // The canvas falls back to the primary mode for note colours, but the HUD must not claim a
        // window matched a mode when the engine said there was not enough evidence.
        var window = Build(ResultWith([], [0, 7], insufficient: true, primaryMode: ScaleMode.Lydian));

        Assert.Null(window.ModeTag);
        Assert.DoesNotContain(window.Degrees, badge => badge.InMode);
    }

    // The mask VALUES are pinned in ModeDefinitionsTests; the hex FORMAT rides along below.
    [Fact]
    public void Alternatives_are_the_ranked_runners_up_without_the_winner()
    {
        IReadOnlyList<ModalMatch> matches =
        [
            new ModalMatch(ScaleMode.Dorian, 0.92, [], []),
            new ModalMatch(ScaleMode.Aeolian, 0.81, [], []),
            new ModalMatch(ScaleMode.MinorPentatonic, 0.60, [], []),
        ];
        var window = Build(ResultWith(matches, [0, 3, 7]));

        Assert.Equal("Dorian", window.ModeTag);
        Assert.Equal("0x089", window.MaskHex); // sung {0,3,7} rendered as three hex digits
        Assert.Equal(0.92, window.ModeConfidence!.Value, precision: 6);
        Assert.Equal(["Aeolian", "MinorPentatonic"], window.Alternatives.Select(a => a.Mode).ToArray());
        Assert.Equal([0.81, 0.60], window.Alternatives.Select(a => a.Confidence).ToArray());
    }
}
