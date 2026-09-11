using PoMode.API.Features.Practice;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Practice;

/// <summary>
/// The phrase a practice attempt is graded against. Two properties matter above all others, and both
/// are about trust rather than musicality: the phrase must be regenerable from the six values an
/// attempt carries — otherwise the grading runs against notes the singer was never shown — and it
/// must be drawn from the mode's own scale, or the exercise teaches the wrong note set.
/// </summary>
public class ModeExerciseBuilderTests
{
    private static ModeExerciseDto Build(
        ModeExerciseKind kind = ModeExerciseKind.ScaleRun,
        ScaleMode mode = ScaleMode.Dorian,
        int tonic = 2,
        double bpm = 72,
        int seed = 42,
        int octave = 4)
        => ModeExerciseBuilder.Build(kind, mode, tonic, bpm, seed, octave);

    [Fact]
    public void The_same_six_values_always_produce_the_same_phrase()
    {
        // The whole attempt protocol rests on this: the browser posts six numbers, not a note list,
        // and the server rebuilds the phrase to grade against.
        var first = Build(ModeExerciseKind.Phrase, seed: 1234);
        var second = Build(ModeExerciseKind.Phrase, seed: 1234);

        Assert.Equal(
            first.TargetNotes.Select(note => (note.MidiPitch, note.StartSec)),
            second.TargetNotes.Select(note => (note.MidiPitch, note.StartSec)));
    }

    [Fact]
    public void A_different_seed_produces_a_different_phrase()
    {
        var first = Build(ModeExerciseKind.Phrase, seed: 1);
        var second = Build(ModeExerciseKind.Phrase, seed: 2);

        Assert.NotEqual(
            first.TargetNotes.Select(note => note.MidiPitch),
            second.TargetNotes.Select(note => note.MidiPitch));
    }

    [Theory]
    [InlineData(ModeExerciseKind.ScaleRun)]
    [InlineData(ModeExerciseKind.CharacteristicLeap)]
    [InlineData(ModeExerciseKind.Phrase)]
    public void Every_note_belongs_to_the_modes_own_scale(ModeExerciseKind kind)
    {
        // D Dorian, so a phrase drawn from the parent key instead of the mode would still pass a
        // naive check — both contain the same seven pitch classes. What it would get wrong is the
        // pentatonics, which is why every mode is checked below as well.
        var exercise = Build(kind);
        var intervals = ScaleModes.Intervals(exercise.Mode);

        Assert.All(exercise.TargetNotes, note =>
            Assert.Contains(((note.MidiPitch - exercise.TonicPitchClass) % 12 + 12) % 12, intervals));
    }

    [Fact]
    public void A_pentatonic_exercise_never_sounds_the_notes_the_scale_omits()
    {
        // The case the parent-key mistake actually shows up in: A minor pentatonic has no 2nd and no
        // 6th, and sounding either is the one thing that makes it not a pentatonic.
        var exercise = Build(ModeExerciseKind.ScaleRun, ScaleMode.MinorPentatonic, tonic: 9);
        var degrees = exercise.TargetNotes
            .Select(note => ((note.MidiPitch - 9) % 12 + 12) % 12)
            .ToHashSet();

        Assert.DoesNotContain(2, degrees);   // the 2nd
        Assert.DoesNotContain(9, degrees);   // the 6th
    }

    [Fact]
    public void The_leap_drill_actually_sounds_the_degree_that_names_the_mode()
    {
        // Dorian's natural 6th is the whole difference between it and Aeolian. A drill that never
        // sang it would be testing nothing.
        var exercise = Build(ModeExerciseKind.CharacteristicLeap, ScaleMode.Dorian, tonic: 2);
        var degrees = exercise.TargetNotes
            .Select(note => ((note.MidiPitch - 2) % 12 + 12) % 12)
            .ToHashSet();

        Assert.Contains(9, degrees);  // natural 6
        Assert.Contains(0, degrees);  // and the tonic it is heard against
    }

    [Fact]
    public void Notes_are_one_per_beat_and_the_duration_matches()
    {
        var exercise = Build(bpm: 60);   // one note per second, so the arithmetic is visible

        Assert.All(exercise.TargetNotes, note => Assert.Equal(1.0, note.DurationSec, precision: 6));
        Assert.Equal(exercise.TargetNotes.Count, exercise.DurationSec, precision: 6);
        Assert.Equal(
            Enumerable.Range(0, exercise.TargetNotes.Count).Select(i => (double)i),
            exercise.TargetNotes.Select(note => note.StartSec));
    }

    [Fact]
    public void An_out_of_range_request_is_clamped_rather_than_refused()
    {
        // A caller sending nonsense gets a singable exercise, not an exception: this is a query
        // string, and every value on it is a slider position somebody could type over.
        var exercise = ModeExerciseBuilder.Build(
            ModeExerciseKind.ScaleRun, ScaleMode.Lydian, tonicPitchClass: -3, bpm: 5000, seed: 1, octave: 99);

        Assert.InRange(exercise.Bpm, 40, 200);
        Assert.InRange(exercise.Octave, 2, 5);
        Assert.Equal(9, exercise.TonicPitchClass);  // -3 wraps to A, not to a negative index
    }

    [Fact]
    public void A_scale_run_climbs_to_the_octave_and_comes_back()
    {
        var exercise = Build(ModeExerciseKind.ScaleRun, ScaleMode.Ionian, tonic: 0);
        var pitches = exercise.TargetNotes.Select(note => note.MidiPitch).ToList();

        Assert.Equal(pitches[0], pitches[^1]);                 // starts and ends home
        Assert.Equal(pitches[0] + 12, pitches.Max());          // reaches the octave
        Assert.Equal(pitches.Max(), pitches[pitches.Count / 2]); // and turns around at the top
    }
}
