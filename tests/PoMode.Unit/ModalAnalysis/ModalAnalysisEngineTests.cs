using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class ModalAnalysisEngineTests
{
    private static NoteEvent At(int midi, double start) => new(midi, start, 0.4, 96);

    [Fact]
    public void ModalAnalysis_ScoresAndRanksDorianCorrectly()
    {
        List<NoteEvent> notes =
        [
            At(62, 0.0), At(64, 0.2), At(65, 0.4), At(67, 0.6),
            At(69, 0.8), At(71, 1.0), At(72, 1.2), At(62, 1.4),
        ];
        List<ChordSpan> chords = [new("Dm7", "D", "min7", 0, 2)];

        var result = ModalAnalysisEngine.Analyze(notes, chords);
        Assert.Equal(2, result.TonicPitchClass);
        Assert.Equal("D", result.TonicName);
        Assert.Equal(ScaleMode.Dorian, result.Windows[0].Matches[0].Mode);
        Assert.Equal(ScaleMode.Dorian, result.PrimaryMode);
        Assert.False(result.Windows[0].InsufficientEvidence);
    }

    [Fact]
    public void ModalAnalysis_HandlesEvidenceThresholdsAndOutsideNotes()
    {
        // Insufficient evidence (< 3 notes)
        var sparse = ModalAnalysisEngine.Analyze([At(60, 0.0), At(67, 0.5)], [new("C", "C", "maj", 0, 2)]);
        Assert.True(sparse.Windows[0].InsufficientEvidence);
        Assert.Empty(sparse.Windows[0].Matches);

        // Outside note (C# in C Ionian)
        var withOutside = ModalAnalysisEngine.Analyze(
            [At(60, 0.0), At(61, 0.2), At(64, 0.4), At(65, 0.6), At(67, 0.8)],
            [new("C", "C", "maj", 0, 2)]);
        var top = withOutside.Windows[0].Matches[0];
        Assert.True(top.Confidence < 1.0);
        Assert.NotEmpty(top.OutsideIntervals);
    }

    [Fact]
    public void ModalAnalysis_WindowsAndEmptyStateStructure()
    {
        var empty = ModalAnalysisEngine.Analyze([], []);
        Assert.Equal(1, empty.SchemaVersion);
        Assert.Equal(120.0, empty.TempoBpm);

        List<NoteEvent> notes = [At(60, 0.1), At(62, 0.3), At(64, 0.5), At(65, 4.1), At(67, 4.3), At(69, 4.5)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2), new("F", "F", "maj", 4, 6)];
        var result = ModalAnalysisEngine.Analyze(notes, chords, tempoBpm: 120.0);
        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(3, result.Windows[0].SungIntervals.Count);
        Assert.Equal(1, result.Windows[0].MeasureNumber);
        Assert.Equal(3, result.Windows[1].MeasureNumber);
    }
}
