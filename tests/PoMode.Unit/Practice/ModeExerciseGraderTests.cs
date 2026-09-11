using PoMode.API.Features.Practice;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Practice;

/// <summary>
/// Scoring a sung attempt. The grader's job is to be believable: a singer who nails the phrase must
/// see 100, a singer who sings nothing must not see a number that looks like a measurement, and the
/// three components must fail independently — because the advice the page gives is chosen from
/// whichever one failed.
/// </summary>
public class ModeExerciseGraderTests
{
    /// <summary>C Ionian at 60 BPM: one note per second, so every timing figure below is readable
    /// as written rather than as a fraction of a beat.</summary>
    private static ModeExerciseDto Exercise() => ModeExerciseBuilder.Build(
        ModeExerciseKind.ScaleRun, ScaleMode.Ionian, tonicPitchClass: 0, bpm: 60, seed: 1, octave: 4);

    /// <summary>The target phrase sung back exactly, optionally transposed and/or shifted in time.</summary>
    private static List<NoteEvent> Sing(
        ModeExerciseDto exercise, int semitones = 0, double offsetSec = 0.0)
        => [.. exercise.TargetNotes.Select(note => note with
        {
            MidiPitch = note.MidiPitch + semitones,
            StartSec = note.StartSec + offsetSec,
        })];

    [Fact]
    public void A_perfect_attempt_scores_full_marks()
    {
        var exercise = Exercise();

        var score = ModeExerciseGrader.Grade(exercise, Sing(exercise));

        Assert.Equal(100, score.Score);
        Assert.Equal(100, score.PitchPercent);
        Assert.Equal(100, score.TimingPercent);
        Assert.Equal(100, score.ModePurityPercent);
        Assert.Equal(exercise.TargetNotes.Count, score.NotesHit);
        Assert.Equal(0, score.ExtraNotes);
        Assert.Empty(score.OutsideNotes);
        Assert.All(score.Notes, note => Assert.Equal(NoteVerdict.Hit, note.Verdict));
    }

    [Fact]
    public void Singing_it_an_octave_down_is_half_credit_not_zero()
    {
        // The ear found every pitch; the voice could not reach the register. Telling a bass that is
        // the same mistake as singing the wrong notes is both wrong and discouraging.
        var exercise = Exercise();

        var score = ModeExerciseGrader.Grade(exercise, Sing(exercise, semitones: -12));

        Assert.All(score.Notes, note => Assert.Equal(NoteVerdict.Octave, note.Verdict));
        Assert.Equal(50, score.PitchPercent);
        Assert.Equal(0, score.NotesHit);
        // Purity and timing are untouched: the pitch classes and the onsets were all correct.
        Assert.Equal(100, score.ModePurityPercent);
        Assert.Equal(100, score.TimingPercent);
    }

    [Fact]
    public void Nothing_sung_reports_an_absence_rather_than_a_measurement()
    {
        var exercise = Exercise();

        var score = ModeExerciseGrader.Grade(exercise, []);

        Assert.Equal(0, score.Score);
        Assert.All(score.Notes, note => Assert.Equal(NoteVerdict.Missed, note.Verdict));
        Assert.Contains("microphone", score.Verdict, StringComparison.OrdinalIgnoreCase);
        // No advice: there is nothing to advise on until something is heard.
        Assert.Null(score.Advice);
    }

    [Fact]
    public void Singing_behind_the_click_costs_timing_and_nothing_else()
    {
        var exercise = Exercise();

        // 0.2s late at 60 BPM: inside the match window, so every note still pairs with its target.
        var score = ModeExerciseGrader.Grade(exercise, Sing(exercise, offsetSec: 0.2));

        Assert.Equal(100, score.PitchPercent);
        Assert.InRange(score.TimingPercent, 35, 50);
        Assert.False(score.Rushing);
        Assert.Equal(0.2, score.MedianTimingOffsetSec, precision: 2);
    }

    [Fact]
    public void Singing_ahead_of_the_click_is_reported_as_rushing()
    {
        var exercise = Exercise();

        var score = ModeExerciseGrader.Grade(exercise, Sing(exercise, offsetSec: -0.2));

        Assert.True(score.Rushing);
        Assert.Equal(-0.2, score.MedianTimingOffsetSec, precision: 2);
        Assert.NotNull(score.Advice);
        Assert.Contains("ahead", score.Advice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_phrase_sung_a_whole_beat_late_is_not_quietly_realigned()
    {
        // The alternative — matching each sung note to the nearest target wherever it lies — would
        // report a phrase that came in a bar late as note-perfect. It is a different performance.
        var exercise = Exercise();

        var score = ModeExerciseGrader.Grade(exercise, Sing(exercise, offsetSec: 1.0));

        Assert.Equal(NoteVerdict.Missed, score.Notes[0].Verdict);
        Assert.True(score.PitchPercent < 40,
            $"a phrase displaced by a whole beat should not score {score.PitchPercent}% on pitch");
    }

    [Fact]
    public void Notes_from_outside_the_mode_are_named_back()
    {
        var exercise = Exercise();
        // Every note a semitone sharp: in C Ionian that is five pitch classes the scale does not have.
        var score = ModeExerciseGrader.Grade(exercise, Sing(exercise, semitones: 1));

        Assert.Equal(0, score.PitchPercent);
        Assert.True(score.ModePurityPercent < 50);
        Assert.NotEmpty(score.OutsideNotes);
        Assert.NotNull(score.Advice);
    }

    [Fact]
    public void Purity_counts_every_note_heard_not_only_the_matched_ones()
    {
        // The habit this feature exists to break: hitting the targets while filling the gaps with
        // notes from the parent key. Matching alone would score that attempt perfect.
        var exercise = ModeExerciseBuilder.Build(
            ModeExerciseKind.CharacteristicLeap, ScaleMode.Dorian, tonicPitchClass: 2, bpm: 60,
            seed: 1, octave: 4);
        var sung = Sing(exercise);
        // A flat 6 (Bb over D) between the targets — Aeolian's degree, not Dorian's, and sung far
        // enough off any target onset to be an extra note rather than a wrong one.
        sung.Add(new NoteEvent(70, exercise.DurationSec + 2.0, 0.5, 90));

        var score = ModeExerciseGrader.Grade(exercise, sung);

        Assert.Equal(100, score.PitchPercent);
        Assert.True(score.ModePurityPercent < 100);
        Assert.Equal(1, score.ExtraNotes);
        Assert.Contains("A#", score.OutsideNotes);  // the app spells with sharps throughout
    }

    [Fact]
    public void The_characteristic_degree_is_reported_whether_or_not_it_was_sung()
    {
        var exercise = ModeExerciseBuilder.Build(
            ModeExerciseKind.CharacteristicLeap, ScaleMode.Dorian, tonicPitchClass: 2, bpm: 60,
            seed: 1, octave: 4);

        Assert.True(ModeExerciseGrader.Grade(exercise, Sing(exercise)).CharacteristicSung);

        // Only the tonic, over and over: in the mode, in time, and telling you nothing about Dorian.
        var tonicOnly = exercise.TargetNotes
            .Select(note => note with { MidiPitch = 62 })
            .ToList();
        var timid = ModeExerciseGrader.Grade(exercise, tonicOnly);

        Assert.False(timid.CharacteristicSung);
        Assert.Equal(100, timid.ModePurityPercent);
        Assert.NotNull(timid.Advice);
    }
}
