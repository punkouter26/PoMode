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
    public void There_is_one_badge_per_pitch_class_in_degree_order()
    {
        var window = Build(ResultWith([new ModalMatch(ScaleMode.Ionian, 0.9, [], [])], [0, 4, 7]));

        Assert.Equal(12, window.Degrees.Count);
        Assert.Equal(
            ["1", "b2", "2", "b3", "3", "4", "#4", "5", "b6", "6", "b7", "7"],
            window.Degrees.Select(badge => badge.Label).ToArray());
    }

    [Fact]
    public void Sung_degrees_are_marked_and_unsung_ones_are_not()
    {
        var window = Build(ResultWith([new ModalMatch(ScaleMode.Ionian, 0.9, [], [])], [0, 4, 7]));

        Assert.Equal([0, 4, 7], window.Degrees.Where(b => b.Sung).Select(b => b.Interval).ToArray());
        Assert.DoesNotContain(1, window.Degrees.Where(b => b.Sung).Select(b => b.Interval));
    }

    [Fact]
    public void In_mode_degrees_follow_the_top_ranked_mode()
    {
        var ionian = Build(ResultWith([new ModalMatch(ScaleMode.Ionian, 0.9, [], [])], [0]));
        Assert.Equal(
            [0, 2, 4, 5, 7, 9, 11],
            ionian.Degrees.Where(b => b.InMode).Select(b => b.Interval).ToArray());

        var phrygian = Build(ResultWith([new ModalMatch(ScaleMode.Phrygian, 0.9, [], [])], [0]));
        Assert.Equal(
            [0, 1, 3, 5, 7, 8, 10],
            phrygian.Degrees.Where(b => b.InMode).Select(b => b.Interval).ToArray());
    }

    [Fact]
    public void Characteristic_degrees_are_flagged_separately_from_plain_in_mode()
    {
        var window = Build(ResultWith([new ModalMatch(ScaleMode.Dorian, 0.9, [], [])], [0]));

        // Dorian's characteristic degrees are the natural 6 (9) over the minor 3 (3).
        Assert.Equal([3, 9], window.Degrees.Where(b => b.Characteristic).Select(b => b.Interval).Order().ToArray());
        Assert.All(window.Degrees.Where(b => b.Characteristic), badge => Assert.True(badge.InMode));
    }

    [Fact]
    public void A_window_with_no_usable_mode_marks_nothing_in_mode_but_still_reports_what_was_sung()
    {
        var window = Build(ResultWith([], [0, 7], insufficient: true, primaryMode: null));

        Assert.True(window.InsufficientEvidence);
        Assert.Null(window.ModeTag);
        Assert.Null(window.ModeConfidence);
        Assert.DoesNotContain(window.Degrees, badge => badge.InMode);
        Assert.DoesNotContain(window.Degrees, badge => badge.Characteristic);
        Assert.Equal([0, 7], window.Degrees.Where(b => b.Sung).Select(b => b.Interval).ToArray());
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

    [Fact]
    public void The_vocal_mask_is_exposed_as_a_three_digit_hex_string()
    {
        // Dorian's full interval set: 0 2 3 5 7 9 10 -> 0x6AD, the value in spec §6's table.
        var window = Build(ResultWith(
            [new ModalMatch(ScaleMode.Dorian, 0.9, [], [])], [0, 2, 3, 5, 7, 9, 10]));

        Assert.Equal("0x6AD", window.MaskHex);
    }

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
        Assert.Equal(0.92, window.ModeConfidence!.Value, precision: 6);
        Assert.Equal(["Aeolian", "MinorPentatonic"], window.Alternatives.Select(a => a.Mode).ToArray());
        Assert.Equal([0.81, 0.60], window.Alternatives.Select(a => a.Confidence).ToArray());
    }

    [Fact]
    public void Window_identity_chord_and_measure_survive_into_the_payload()
    {
        var window = Build(ResultWith([new ModalMatch(ScaleMode.Ionian, 0.9, [], [])], [0]));

        Assert.Equal(0, window.Index);
        Assert.Equal("Dm", window.ChordSymbol);
        Assert.Equal(3, window.MeasureNumber);
        Assert.Equal(0.0, window.StartSec);
        Assert.Equal(4.0, window.EndSec);
    }

    [Fact]
    public void Degree_labels_are_relative_to_the_tonic_so_a_non_c_tonic_still_starts_at_one()
    {
        var window = Build(ResultWith(
            [new ModalMatch(ScaleMode.Ionian, 0.9, [], [])], [0], tonicPitchClass: 7));

        Assert.Equal("1", window.Degrees[0].Label);
        Assert.True(window.Degrees[0].Sung);
    }
}
