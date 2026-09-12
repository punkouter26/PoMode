using PoMode.API.Features.Analysis;
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

}
