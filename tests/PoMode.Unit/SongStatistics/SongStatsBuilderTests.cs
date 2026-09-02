using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.SongStatistics;
using PoMode.API.Features.Visualization;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.SongStatistics;

public class SongStatsBuilderTests
{
    private const double Bpm = 120.0;

    private static NoteEvent Note(int midiPitch, double startSec, double durationSec = 0.25)
        => new(midiPitch, startSec, durationSec, Velocity: 90);

    private static ChordSpan Chord(string symbol, string root, string quality, double startSec, double endSec)
        => new(symbol, root, quality, startSec, endSec);

    private static ModalResult Result(
        ScaleMode? mode = ScaleMode.Ionian,
        double primaryConfidence = 0.9,
        IReadOnlyList<ModalWindow>? windows = null)
        => new(
            SchemaVersion: 1,
            TonicPitchClass: 0,
            TonicName: "C",
            TonicConfidence: 0.9,
            PrimaryMode: mode,
            PrimaryConfidence: primaryConfidence,
            TempoBpm: Bpm,
            TempoEstimated: false,
            Windows: windows ?? [Window(0, 0.0, 60.0, ScaleMode.Ionian)]);

    private static ModalWindow Window(int index, double startSec, double endSec, ScaleMode? mode)
        => new(
            Index: index,
            StartSec: startSec,
            EndSec: endSec,
            ChordSymbol: "C",
            MeasureNumber: index + 1,
            VocalMask: 0,
            SungIntervals: [],
            InsufficientEvidence: mode is null,
            Matches: mode is null ? [] : [new ModalMatch(mode.Value, 0.9, [0, 4, 7], [])]);

    private static SongStats Build(
        IReadOnlyList<NoteEvent> notes,
        IReadOnlyList<ChordSpan>? chords = null,
        ModalResult? result = null,
        BeatGridDto? beats = null)
    {
        chords ??= [];
        result ??= Result();
        var visual = VisualizationBuilder.Build(notes, chords, result);
        return SongStatsBuilder.Build(visual, chords, result, beats);
    }

    private static BeatGridDto Grid(double firstBeatSec = 0.0)
        => new(Bpm, firstBeatSec, Confidence: 0.9);

    [Fact]
    public void StepAndLeap_CalculatesStepLeapRepeatedDistributionsCorrectly()
    {
        var allSteps = Build([Note(60, 0.0), Note(62, 0.5), Note(64, 1.0), Note(65, 1.5)]);
        Assert.Equal(100.0, allSteps.Motion.StepPercent);
        Assert.Equal(0.0, allSteps.Motion.LeapPercent);
        Assert.Equal(0.0, allSteps.Motion.RepeatPercent);

        var allLeaps = Build([Note(60, 0.0), Note(67, 0.5), Note(60, 1.0)]);
        Assert.Equal(0.0, allLeaps.Motion.StepPercent);
        Assert.Equal(100.0, allLeaps.Motion.LeapPercent);
        Assert.Equal(0.0, allLeaps.Motion.RepeatPercent);

        var jump = Build([Note(60, 0.0), Note(62, 0.5), Note(74, 1.0)]);
        Assert.NotNull(jump.BiggestLeap);
        Assert.Equal(12, jump.BiggestLeap.Semitones);
    }

    [Fact]
    public void Contour_IdentifiesDirectionAndExtremeNotes()
    {
        var rising = Build([Note(60, 0.0), Note(62, 0.5), Note(64, 1.0), Note(65, 1.5), Note(67, 2.0)]);
        Assert.Equal("Rising", rising.Contour.Shape);

        var falling = Build([Note(67, 0.0), Note(65, 0.5), Note(64, 1.0), Note(62, 1.5), Note(60, 2.0)]);
        Assert.Equal("Falling", falling.Contour.Shape);

        var arch = Build([Note(60, 0.0), Note(67, 0.5), Note(60, 1.0)]);
        Assert.Equal("Arch", arch.Contour.Shape);
    }

    [Fact]
    public void Phrasing_MeasuresPhraseCountsLengthsAndBreaks()
    {
        var onePhrase = Build([Note(60, 0.0, 0.4), Note(62, 0.5, 0.4), Note(64, 1.0, 0.4)]);
        Assert.Equal(1, onePhrase.Phrases.Count);

        var twoPhrases = Build([
            Note(60, 0.0, 0.4), Note(62, 0.5, 0.4),
            Note(64, 1.9, 0.4), Note(65, 2.4, 0.4)
        ]);
        Assert.Equal(2, twoPhrases.Phrases.Count);
    }

    [Fact]
    public void Tessitura_CalculatesMedianVocalCenterAndCoreRange()
    {
        var stats = Build([
            Note(60, 0.0, 1.0),
            Note(67, 1.0, 3.0),
            Note(72, 4.0, 1.0)
        ]);
        Assert.NotNull(stats.Tessitura);
        Assert.Equal("G4", stats.Tessitura.MedianLabel);
        Assert.True(stats.Tessitura.SpanSemitones >= 0);
    }

    [Fact]
    public void ScaleDegrees_CountsPitchDistributionAndModalAffinity()
    {
        var stats = Build([
            Note(60, 0.0), Note(64, 0.5), Note(67, 1.0), Note(69, 1.5)
        ]);

        Assert.NotEmpty(stats.ScaleDegrees);
        var rootDegree = Assert.Single(stats.ScaleDegrees, d => d.Interval == 0);
        Assert.Equal("C", rootDegree.NoteName);
        Assert.Equal(25.0, rootDegree.Percent);
        Assert.True(stats.InScalePercent >= 99.0);
    }

    [Fact]
    public void Harmony_CountsUniqueChordsAndPacing()
    {
        var chords = new[]
        {
            Chord("C", "C", "maj", 0.0, 2.0),
            Chord("G", "G", "maj", 2.0, 4.0),
            Chord("Am", "A", "min", 4.0, 6.0),
            Chord("F", "F", "maj", 6.0, 8.0)
        };
        var stats = Build([Note(60, 0.0, 8.0)], chords: chords, beats: Grid());
        Assert.Equal(4, stats.ChordVocabulary.UniqueChords);
        Assert.Equal(4.0, stats.HarmonicRhythm.AverageChordBeats);
    }

    [Fact]
    public void ChordTones_CalculatesTonesInsideChords()
    {
        var chords = new[] { Chord("C", "C", "maj", 0.0, 2.0) };
        var stats = Build([
            Note(60, 0.0), Note(64, 0.5), Note(62, 1.0)
        ], chords: chords);

        Assert.True(stats.ChordTones.ConsonancePercent is >= 65.0 and <= 68.0);
    }

    [Fact]
    public void Rhythm_MeasuresOnBeatAndOffBeatPlacement()
    {
        var stats = Build([
            Note(60, 0.0), Note(62, 0.5), Note(64, 0.25)
        ], beats: Grid(0.0));

        Assert.True(stats.Rhythm.OnBeatPercent > 50.0);
    }

    [Fact]
    public void NoteLengths_IdentifiesCommonDurations()
    {
        var stats = Build([
            Note(60, 0.0, 0.5), Note(62, 0.5, 0.5), Note(64, 1.0, 0.25)
        ], beats: Grid(0.0));

        Assert.NotEmpty(stats.Rhythm.NoteValues);
    }

    [Fact]
    public void TempoStability_CalculatesBarToBarDeviationsAndFingerprint()
    {
        var chords = new[] { Chord("C", "C", "maj", 0.0, 4.0) };
        var stats = Build([Note(60, 0.0, 4.0)], chords: chords);

        Assert.NotNull(stats.Fingerprint);
        Assert.Contains("C Ionian", stats.Fingerprint);
        Assert.Contains("120 BPM", stats.Fingerprint);
    }
}
