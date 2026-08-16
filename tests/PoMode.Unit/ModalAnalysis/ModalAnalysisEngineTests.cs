using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

public class ModalAnalysisEngineTests
{
    private static NoteEvent At(int midi, double start) => new(midi, start, 0.4, 96);

    [Fact]
    public void D_dorian_material_scores_dorian_top()
    {
        // Tonic D (62). Sing D E F G A B C — the natural 6 (B) is the Dorian marker.
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
    public void Window_with_two_pitch_classes_reports_insufficient_evidence()
    {
        List<NoteEvent> notes = [At(60, 0.0), At(67, 0.5)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2)];

        var result = ModalAnalysisEngine.Analyze(notes, chords);

        Assert.True(result.Windows[0].InsufficientEvidence);
        Assert.Empty(result.Windows[0].Matches);
        Assert.Null(result.PrimaryMode);
    }

    [Fact]
    public void Notes_are_assigned_to_the_window_containing_their_start()
    {
        List<NoteEvent> notes = [At(60, 0.5), At(62, 0.9), At(64, 1.1), At(65, 2.5), At(67, 2.9), At(69, 3.1)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2), new("F", "F", "maj", 2, 4)];

        var result = ModalAnalysisEngine.Analyze(notes, chords);

        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(3, result.Windows[0].SungIntervals.Count);
        Assert.Equal(3, result.Windows[1].SungIntervals.Count);
    }

    [Fact]
    public void Vocal_mask_matches_the_sung_intervals()
    {
        List<NoteEvent> notes = [At(60, 0.0), At(62, 0.3), At(64, 0.6), At(65, 0.9)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2)];

        var window = ModalAnalysisEngine.Analyze(notes, chords).Windows[0];

        var expected = window.SungIntervals.Aggregate(0, (mask, interval) => mask | (1 << interval));
        Assert.Equal(expected, window.VocalMask);
    }

    [Fact]
    public void Measure_numbers_follow_the_tempo_at_four_four()
    {
        // 120 BPM ⇒ 2 s per measure.
        List<NoteEvent> notes = [At(60, 0.1), At(62, 0.3), At(64, 0.5), At(65, 4.1), At(67, 4.3), At(69, 4.5)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2), new("F", "F", "maj", 4, 6)];

        var result = ModalAnalysisEngine.Analyze(notes, chords, tempoBpm: 120.0);

        Assert.Equal(1, result.Windows[0].MeasureNumber);
        Assert.Equal(3, result.Windows[1].MeasureNumber);
        Assert.Equal(120.0, result.TempoBpm);
        Assert.True(result.TempoEstimated);
    }

    [Fact]
    public void Matches_are_ranked_and_capped_at_four()
    {
        List<NoteEvent> notes = [At(60, 0.0), At(62, 0.2), At(64, 0.4), At(65, 0.6), At(67, 0.8), At(69, 1.0), At(71, 1.2)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2)];

        var matches = ModalAnalysisEngine.Analyze(notes, chords).Windows[0].Matches;

        Assert.InRange(matches.Count, 1, 4);
        Assert.Equal(ScaleMode.Ionian, matches[0].Mode);
        for (var i = 1; i < matches.Count; i++)
        {
            Assert.True(matches[i - 1].Confidence >= matches[i].Confidence);
        }
    }

    [Fact]
    public void Outside_notes_are_reported_and_cap_confidence_below_one()
    {
        // C Ionian plus a b2 (C#) that no Ionian scale contains.
        List<NoteEvent> notes = [At(60, 0.0), At(61, 0.2), At(64, 0.4), At(65, 0.6), At(67, 0.8)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2)];

        var top = ModalAnalysisEngine.Analyze(notes, chords).Windows[0].Matches[0];

        Assert.True(top.Confidence < 1.0);
        Assert.NotEmpty(top.OutsideIntervals);
    }

    [Fact]
    public void Result_carries_schema_version_one()
        => Assert.Equal(1, ModalAnalysisEngine.Analyze([], []).SchemaVersion);
}
