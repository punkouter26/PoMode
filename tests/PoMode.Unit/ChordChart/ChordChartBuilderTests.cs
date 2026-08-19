using PoMode.API.Features.ChordChart;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ChordChart;

public class ChordChartBuilderTests
{
    private static ModalResult Result(double bpm = 120) => new(
        SchemaVersion: 1, TonicPitchClass: 0, TonicName: "C", TonicConfidence: 1.0,
        PrimaryMode: ScaleMode.Ionian, PrimaryConfidence: 0.9,
        TempoBpm: bpm, TempoEstimated: false, Windows: []);

    [Fact]
    public void Four_two_second_chords_at_120_bpm_render_one_line_of_four_measures()
    {
        // 120 BPM in 4/4 → 2 s per measure, so each chord fills exactly one measure.
        IReadOnlyList<ChordSpan> chords =
        [
            new("C", "C", "maj", 0, 2),
            new("G", "G", "maj", 2, 4),
            new("Am", "A", "min", 4, 6),
            new("F", "F", "maj", 6, 8),
        ];

        var chart = ChordChartBuilder.Build(chords, Result(), "song.mp3");

        Assert.Contains("song.mp3", chart);
        Assert.Contains("Key: C Ionian", chart);
        Assert.Contains("Tempo: 120 BPM", chart);
        Assert.Contains("| C    | G    | Am   | F    |", chart);
    }

    [Fact]
    public void A_held_chord_repeats_as_dots_and_gaps_read_no_chord()
    {
        // One C chord held 4 s (2 measures), then 4 s of silence (2 measures of N.C.).
        IReadOnlyList<ChordSpan> chords = [new("C", "C", "maj", 0, 4), new("G", "G", "maj", 6, 8)];

        var chart = ChordChartBuilder.Build(chords, Result(), "held");

        Assert.Contains("| C    | .    | N.C. | G    |", chart);
    }

    [Fact]
    public void No_chords_yields_an_honest_message_not_an_empty_grid()
    {
        var chart = ChordChartBuilder.Build([], Result(), "empty");

        Assert.Contains("No chords were detected", chart);
    }
}
