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
    public void Windows_take_the_notes_that_start_in_them_and_measure_numbers_follow_the_tempo()
    {
        // 120 BPM ⇒ 2 s per measure; three notes start inside each chord's span.
        List<NoteEvent> notes = [At(60, 0.1), At(62, 0.3), At(64, 0.5), At(65, 4.1), At(67, 4.3), At(69, 4.5)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2), new("F", "F", "maj", 4, 6)];

        var result = ModalAnalysisEngine.Analyze(notes, chords, tempoBpm: 120.0);

        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(3, result.Windows[0].SungIntervals.Count);
        Assert.Equal(3, result.Windows[1].SungIntervals.Count);
        Assert.Equal(1, result.Windows[0].MeasureNumber);
        Assert.Equal(3, result.Windows[1].MeasureNumber);
        Assert.Equal(120.0, result.TempoBpm);
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
    public void An_empty_analysis_still_carries_the_schema_version_and_the_estimated_default_tempo()
    {
        var result = ModalAnalysisEngine.Analyze([], []);

        Assert.Equal(1, result.SchemaVersion);
        Assert.Equal(120.0, result.TempoBpm);
        Assert.True(result.TempoEstimated);
        // A real measured tempo must not be mislabelled as an estimate.
        Assert.False(ModalAnalysisEngine.Analyze([], [], tempoBpm: 96.0, tempoEstimated: false).TempoEstimated);
    }

    [Fact]
    public void Better_coverage_always_outranks_a_characteristic_bonus()
    {
        // Tonic C. Seven distinct sung classes incl. a chromatic b2 and no 7th:
        // C Db D E F G A -> intervals {0,1,2,4,5,7,9}.
        // Ionian explains all but the Db; MajorPentatonic leaves both Db and F unexplained.
        // The mode that explains MORE of what was sung must rank higher.
        List<NoteEvent> notes =
        [
            At(60, 0.0), At(61, 0.2), At(62, 0.4), At(64, 0.6),
            At(65, 0.8), At(67, 1.0), At(69, 1.2),
        ];
        List<ChordSpan> chords = [new("C", "C", "maj", 0, 2)];

        var matches = ModalAnalysisEngine.Analyze(notes, chords).Windows[0].Matches;

        var ionian = matches.SingleOrDefault(m => m.Mode == ScaleMode.Ionian);
        var pentatonic = matches.SingleOrDefault(m => m.Mode == ScaleMode.MajorPentatonic);
        Assert.NotNull(ionian);
        if (pentatonic is not null)
        {
            Assert.True(
                ionian.Confidence > pentatonic.Confidence,
                $"Ionian {ionian.Confidence} must beat MajorPentatonic {pentatonic.Confidence}");
        }
        Assert.True(ionian.OutsideIntervals.Count < 2);
    }
}
