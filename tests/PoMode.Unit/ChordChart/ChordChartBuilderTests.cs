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
    public void ChordChartBuilder_RendersAccurateGridAndHandlesEmptyState()
    {
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

        var emptyChart = ChordChartBuilder.Build([], Result(), "empty");
        Assert.Contains("No chords were detected", emptyChart);
    }
}
