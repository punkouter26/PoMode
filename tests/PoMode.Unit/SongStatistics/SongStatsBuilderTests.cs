using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.SongStatistics;
using PoMode.API.Features.Visualization;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.SongStatistics;

/// <summary>
/// The statistics are the only thing an LLM interpreter is allowed to talk about, so every figure
/// here is checked against a hand-built melody whose answer is known by construction.
/// </summary>
public class SongStatsBuilderTests
{
    private const double Bpm = 120.0;

    /// <summary>120 BPM is a beat every half second — every rhythm expectation below assumes it.</summary>
    private const double BeatSec = 0.5;

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

    // ---- 1. Step vs leap -------------------------------------------------------------------

    [Fact]
    public void Motion_splits_repeats_steps_and_leaps_at_the_conventional_boundary()
    {
        // 60 -> 60 repeat, -> 62 step (2), -> 65 leap (3). Three intervals from four notes.
        var stats = Build([Note(60, 0.0), Note(60, 1.0), Note(62, 2.0), Note(65, 3.0)]);

        Assert.Equal(3, stats.Motion.IntervalCount);
        Assert.Equal(1, stats.Motion.RepeatCount);
        Assert.Equal(1, stats.Motion.StepCount);
        Assert.Equal(1, stats.Motion.LeapCount);
        Assert.Equal(5.0 / 3.0, stats.Motion.AverageIntervalSemitones, 6);
    }

    [Fact]
    public void Motion_is_empty_rather_than_dividing_by_zero_for_a_single_note()
    {
        var stats = Build([Note(60, 0.0)]);

        Assert.Equal(0, stats.Motion.IntervalCount);
        Assert.Equal(0, stats.Motion.StepPercent);
        Assert.Null(stats.BiggestLeap);
    }

    // ---- 2. Biggest leap -------------------------------------------------------------------

    [Fact]
    public void Biggest_leap_reports_the_widest_jump_with_its_direction_and_time()
    {
        var stats = Build([Note(60, 0.0), Note(72, 1.0), Note(71, 2.0)]);

        var leap = Assert.IsType<LeapHighlight>(stats.BiggestLeap);
        Assert.Equal(12, leap.Semitones);
        Assert.True(leap.Ascending);
        Assert.Equal(1.0, leap.AtSec);
        Assert.Equal("C4", leap.FromLabel);
        Assert.Equal("C5", leap.ToLabel);
    }

    [Fact]
    public void Biggest_leap_is_null_when_the_melody_never_changes_pitch()
    {
        var stats = Build([Note(60, 0.0), Note(60, 1.0), Note(60, 2.0)]);

        Assert.Null(stats.BiggestLeap);
    }

    // ---- 3. Contour ------------------------------------------------------------------------

    [Fact]
    public void Contour_names_a_rising_line()
    {
        var stats = Build([.. Enumerable.Range(0, 9).Select(i => Note(60 + i, i))]);

        Assert.Equal("Rising", stats.Contour.Shape);
        Assert.Equal(8, stats.Contour.NetSemitones);
        Assert.Equal(100.0, stats.Contour.RisingPercent);
    }

    [Fact]
    public void Contour_names_an_arch_when_the_middle_sits_above_both_ends()
    {
        // Up then back down to the same pitch: net zero, but plainly an arch.
        int[] pitches = [60, 62, 64, 72, 74, 72, 64, 62, 60];
        var stats = Build([.. pitches.Select((pitch, i) => Note(pitch, i))]);

        Assert.Equal("Arch", stats.Contour.Shape);
        Assert.Equal(0, stats.Contour.NetSemitones);
    }

    // ---- 4. Tessitura ----------------------------------------------------------------------

    [Fact]
    public void Tessitura_ignores_a_single_outlier_high_note()
    {
        // Twenty notes at C4 and one showpiece C6. The 90th percentile must stay at C4.
        var notes = new List<NoteEvent>();
        for (var i = 0; i < 20; i++)
        {
            notes.Add(Note(60, i));
        }
        notes.Add(Note(84, 20));

        var voice = Assert.IsType<TessituraProfile>(Build(notes).Tessitura);
        Assert.Equal(60, voice.MedianMidi);
        Assert.Equal("C4", voice.MedianLabel);
        Assert.Equal(60, voice.HighMidi);
        Assert.Equal(0, voice.SpanSemitones);
    }

    // ---- 5/6. Rhythm -----------------------------------------------------------------------

    [Fact]
    public void Rhythm_is_reported_unusable_rather_than_zero_when_there_is_no_beat_grid()
    {
        var stats = Build([Note(60, 0.0), Note(62, 0.5)], beats: null);

        Assert.False(stats.Rhythm.BeatGridUsable);
        Assert.Empty(stats.Rhythm.NoteValues);
    }

    [Fact]
    public void Rhythm_separates_on_beat_onsets_from_off_beat_ones()
    {
        // Beats at 0.0, 0.5, 1.0 ... so 0.0 and 0.5 are on the beat and 0.25 is the "and".
        var stats = Build(
            [Note(60, 0.0), Note(62, 0.25), Note(64, 0.5)],
            beats: Grid());

        Assert.True(stats.Rhythm.BeatGridUsable);
        Assert.Equal(200.0 / 3.0, stats.Rhythm.OnBeatPercent, 6);
        Assert.Equal(100.0 / 3.0, stats.Rhythm.SyncopationPercent, 6);
    }

    [Fact]
    public void Rhythm_buckets_note_lengths_against_the_beat()
    {
        // At 120 BPM a beat is 0.5s, so 0.5s is a quarter and 0.25s is an eighth.
        var stats = Build(
            [Note(60, 0.0, 0.5), Note(62, 1.0, 0.25)],
            beats: Grid());

        var labels = stats.Rhythm.NoteValues.ToDictionary(bucket => bucket.Label, bucket => bucket.Count);
        Assert.Equal(1, labels["Quarter"]);
        Assert.Equal(1, labels["Eighth"]);
    }

    [Fact]
    public void Rhythm_handles_onsets_before_the_first_beat_without_a_negative_phase()
    {
        // First beat at 1.0s, so the grid extends backwards to 0.5 and 0.0. A note at 0.0 is exactly
        // two beats early and must still count as on the beat; 0.25 is an "and" and must not.
        var stats = Build(
            [Note(60, 0.0), Note(62, 0.25), Note(64, 1.0)],
            beats: Grid(firstBeatSec: 1.0));

        Assert.True(stats.Rhythm.BeatGridUsable);
        Assert.Equal(200.0 / 3.0, stats.Rhythm.OnBeatPercent, 6);
        Assert.Equal(100.0 / 3.0, stats.Rhythm.SyncopationPercent, 6);
    }

    [Fact]
    public void Rhythm_counts_an_onset_that_is_neither_on_the_beat_nor_on_the_and_as_neither()
    {
        // A fifth of a beat late: too far from the beat to be on it, too far from the midpoint to be
        // syncopation. The two percentages describe what they claim and need not sum to 100.
        var stats = Build([Note(60, 0.1)], beats: Grid());

        Assert.Equal(0.0, stats.Rhythm.OnBeatPercent);
        Assert.Equal(0.0, stats.Rhythm.SyncopationPercent);
    }

    // ---- 7. Phrases ------------------------------------------------------------------------

    [Fact]
    public void Phrases_split_on_a_rest_longer_than_the_gap_threshold()
    {
        // Two notes, a 2s rest, then two more.
        var stats = Build([Note(60, 0.0), Note(62, 0.3), Note(64, 3.0), Note(65, 3.3)]);

        Assert.Equal(2, stats.Phrases.Count);
        Assert.Equal(2.0, stats.Phrases.AverageNotes);
        Assert.Equal(0.55, stats.Phrases.AverageSec, 6);
    }

    [Fact]
    public void Phrases_stay_as_one_when_every_gap_is_inside_the_threshold()
    {
        var stats = Build([Note(60, 0.0), Note(62, 0.3), Note(64, 0.6)]);

        Assert.Equal(1, stats.Phrases.Count);
        Assert.Equal(3, stats.Phrases.AverageNotes);
    }

    // ---- 8. Harmonic rhythm ----------------------------------------------------------------

    [Fact]
    public void Harmonic_rhythm_reports_chord_length_in_seconds_and_beats()
    {
        var chords = new[]
        {
            Chord("C", "C", "maj", 0.0, 2.0),
            Chord("F", "F", "maj", 2.0, 4.0),
        };

        var stats = Build([Note(60, 0.0)], chords, beats: Grid());

        Assert.True(stats.HarmonicRhythm.BeatGridUsable);
        Assert.Equal(2.0, stats.HarmonicRhythm.AverageChordSec, 6);
        Assert.Equal(2.0 / BeatSec, stats.HarmonicRhythm.AverageChordBeats, 6);
    }

    // ---- 9. Chord vocabulary ---------------------------------------------------------------

    [Fact]
    public void Chord_vocabulary_counts_distinct_chords_and_ignores_a_chord_repeating_itself()
    {
        var chords = new[]
        {
            Chord("C", "C", "maj", 0.0, 1.0),
            Chord("C", "C", "maj", 1.0, 2.0), // held: not a move
            Chord("G", "G", "maj", 2.0, 3.0),
            Chord("C", "C", "maj", 3.0, 4.0),
            Chord("G", "G", "maj", 4.0, 5.0),
        };

        var vocabulary = Build([Note(60, 0.0)], chords).ChordVocabulary;

        Assert.Equal(2, vocabulary.UniqueChords);
        Assert.Equal("C", vocabulary.TopChords[0].Symbol);
        Assert.Equal(3, vocabulary.TopChords[0].Count);

        var top = vocabulary.TopMoves[0];
        Assert.Equal("C", top.From);
        Assert.Equal("G", top.To);
        Assert.Equal(2, top.Count);
    }

    // ---- 10. Melody against the chord ------------------------------------------------------

    [Fact]
    public void Chord_tones_classify_the_melody_by_degree_of_the_sounding_chord()
    {
        // Over a C major chord: C is the root, E the third, G the fifth, D a tension.
        var chords = new[] { Chord("C", "C", "maj", 0.0, 8.0) };
        var stats = Build([Note(60, 0.0), Note(64, 1.0), Note(67, 2.0), Note(62, 3.0)], chords);

        Assert.Equal(4, stats.ChordTones.ClassifiedNotes);
        Assert.Equal(25.0, stats.ChordTones.RootPercent);
        Assert.Equal(25.0, stats.ChordTones.ThirdPercent);
        Assert.Equal(25.0, stats.ChordTones.FifthPercent);
        Assert.Equal(25.0, stats.ChordTones.TensionPercent);
        Assert.Equal(75.0, stats.ChordTones.ConsonancePercent);
    }

    [Fact]
    public void Chord_tones_exclude_notes_with_no_chord_underneath_rather_than_calling_them_tension()
    {
        var chords = new[] { Chord("C", "C", "maj", 0.0, 1.0) };
        var stats = Build([Note(60, 0.0), Note(62, 5.0)], chords);

        Assert.Equal(1, stats.ChordTones.ClassifiedNotes);
        Assert.Equal(100.0, stats.ChordTones.RootPercent);
        Assert.Equal(0.0, stats.ChordTones.TensionPercent);
    }

    // ---- Modulations -----------------------------------------------------------------------

    [Fact]
    public void Modulations_count_changes_and_skip_windows_with_no_evidence()
    {
        // Ionian, (no evidence), Ionian, Dorian — one real change, not three.
        var result = Result(windows:
        [
            Window(0, 0.0, 1.0, ScaleMode.Ionian),
            Window(1, 1.0, 2.0, mode: null),
            Window(2, 2.0, 3.0, ScaleMode.Ionian),
            Window(3, 3.0, 4.0, ScaleMode.Dorian),
        ]);

        Assert.Equal(1, Build([Note(60, 0.0)], result: result).ModulationCount);
    }

    // ---- Charts -----------------------------------------------------------------------------

    [Fact]
    public void Mode_votes_tally_the_windows_each_mode_won_and_flag_the_primary()
    {
        // Ionian wins two windows, Dorian one; one window has no usable evidence and must not
        // count against anyone or inflate the denominator.
        var result = Result(ScaleMode.Ionian, windows:
        [
            Window(0, 0.0, 1.0, ScaleMode.Ionian),
            Window(1, 1.0, 2.0, ScaleMode.Dorian),
            Window(2, 2.0, 3.0, ScaleMode.Ionian),
            Window(3, 3.0, 4.0, mode: null),
        ]);

        var votes = Build([Note(60, 0.0)], result: result).ModeVotes;

        Assert.Equal(2, votes.Count);
        Assert.Equal("Ionian", votes[0].Mode);
        Assert.Equal(2, votes[0].WindowsWon);
        Assert.True(votes[0].IsPrimary);
        // 2 of 3 decided windows, not 2 of 4.
        Assert.Equal(200.0 / 3.0, votes[0].WindowPercent, 6);
        Assert.Equal("Dorian", votes[1].Mode);
        Assert.False(votes[1].IsPrimary);
    }

    [Fact]
    public void Mode_votes_name_the_degrees_that_identify_each_mode()
    {
        var votes = Build([Note(60, 0.0)],
            result: Result(ScaleMode.Dorian, windows: [Window(0, 0.0, 4.0, ScaleMode.Dorian)])).ModeVotes;

        // The evidence for Dorian is its natural 6th — the chart's tooltip shows exactly this.
        Assert.Contains("6", Assert.Single(votes).CharacteristicDegrees);
    }

    [Fact]
    public void Mode_votes_are_empty_when_no_window_had_usable_evidence()
    {
        var result = Result(mode: null, windows: [Window(0, 0.0, 4.0, mode: null)]);

        Assert.Empty(Build([Note(60, 0.0)], result: result).ModeVotes);
    }

    [Fact]
    public void Scale_degrees_always_return_all_twelve_in_order_including_unsung_ones()
    {
        // Only the tonic is sung; the eleven silent degrees must still be reported, because the
        // gaps are what show the shape of the scale.
        var degrees = Build([Note(60, 0.0), Note(72, 1.0)]).ScaleDegrees;

        Assert.Equal(12, degrees.Count);
        Assert.Equal([.. Enumerable.Range(0, 12)], [.. degrees.Select(degree => degree.Interval)]);
        Assert.Equal(2, degrees[0].NoteCount);           // C4 and C5 are both the tonic
        Assert.Equal(100.0, degrees[0].Percent);
        Assert.Equal(0, degrees[1].NoteCount);
    }

    [Fact]
    public void Scale_degrees_label_each_one_by_note_name_and_degree()
    {
        // Tonic C: interval 4 is E, the major third.
        var degrees = Build([Note(64, 0.0)]).ScaleDegrees;

        Assert.Equal("C", degrees[0].NoteName);
        Assert.Equal("E", degrees[4].NoteName);
        Assert.Equal("3", degrees[4].DegreeLabel);
        Assert.Equal(1, degrees[4].NoteCount);
    }

    [Fact]
    public void Scale_degrees_mark_membership_and_the_identifying_degrees_of_the_primary_mode()
    {
        var degrees = Build([Note(60, 0.0)],
            result: Result(ScaleMode.Dorian, windows: [Window(0, 0.0, 4.0, ScaleMode.Dorian)])).ScaleDegrees;

        // Dorian is 0 2 3 5 7 9 10: the b3 and the natural 6 are in it, the major 3rd is not.
        Assert.True(degrees[3].InPrimaryMode);
        Assert.True(degrees[9].InPrimaryMode);
        Assert.False(degrees[4].InPrimaryMode);
        // The natural 6th is what separates Dorian from Aeolian, so it must be flagged.
        Assert.True(degrees[9].IsCharacteristic);
        Assert.False(degrees[0].IsCharacteristic);
    }

    // ---- Fingerprint -----------------------------------------------------------------------

    [Fact]
    public void Fingerprint_states_the_key_the_tempo_and_the_note_count()
    {
        var stats = Build(
            [Note(60, 0.0), Note(62, 0.5), Note(64, 1.0)],
            [Chord("C", "C", "maj", 0.0, 4.0)],
            beats: Grid());

        Assert.Contains("C Ionian", stats.Fingerprint, StringComparison.Ordinal);
        Assert.Contains("120 BPM", stats.Fingerprint, StringComparison.Ordinal);
        Assert.Contains("3 notes", stats.Fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_says_the_mode_is_unclear_rather_than_naming_a_low_confidence_guess()
    {
        var stats = Build([Note(60, 0.0)], result: Result(ScaleMode.Ionian, primaryConfidence: 0.2));

        Assert.Contains("mode unclear", stats.Fingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain("C Ionian", stats.Fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_omits_the_rhythm_sentence_entirely_when_there_is_no_beat_grid()
    {
        var stats = Build([Note(60, 0.0), Note(62, 0.5)], beats: null);

        Assert.DoesNotContain("on the beat", stats.Fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Fingerprint_says_so_plainly_when_nothing_was_transcribed()
    {
        var stats = Build([]);

        Assert.Contains("No melody was transcribed", stats.Fingerprint, StringComparison.Ordinal);
        Assert.Equal(0, stats.MelodyNoteCount);
    }

    /// <summary>
    /// Guards the invariant the LLM prompt depends on: the paragraph handed to a model must never
    /// contain a locale-specific decimal comma, which a model reads as a second number.
    /// </summary>
    [Fact]
    public void Fingerprint_uses_invariant_number_formatting()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var stats = Build(
                [Note(60, 0.0), Note(62, 0.4), Note(64, 0.8)],
                [Chord("C", "C", "maj", 0.0, 2.5)],
                beats: Grid());

            // Assert the property, not one formatted literal: no decimal in the paragraph may use a
            // comma, whatever the sentence order happens to be.
            Assert.Matches(@"\d\.\d", stats.Fingerprint);
            Assert.DoesNotMatch(@"\d,\d", stats.Fingerprint);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
