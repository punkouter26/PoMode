using System.Globalization;
using System.Text;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// The no-model interpreter: expands the computed statistics into prose with fixed rules. Always
/// available, never wrong, never surprising — and never insightful, which is the trade.
///
/// <para>It is not a Fake placeholder in the <see cref="Pipeline.IStageExecutor.IsPlaceholder"/>
/// sense, because it fabricates nothing: every sentence is a direct reading of a measured number.
/// It is flagged as a classic fallback instead, which ranks it after a real local model and before
/// the paid cloud tier — the correct order for something honest but unclever. That also means the
/// "USING MOCK DATA" banner is not triggered by an interpretation, which would be a lie.</para>
/// </summary>
public sealed class TemplateSongInterpreter : ISongInterpreter
{
    public string Name => nameof(TemplateSongInterpreter);

    public ExecutionTier Tier => ExecutionTier.Local;

    /// <summary>Ranks behind a real local LLM, ahead of the paid cloud one. See the class remarks.</summary>
    public bool IsClassicFallback => true;

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<string> InterpretAsync(SongStats stats, CancellationToken ct)
        => Task.FromResult(Write(stats));

    /// <summary>
    /// Answers a follow-up by routing it to the measurement it is about.
    ///
    /// <para>Not a conversation, and it does not pretend to be one — the history is ignored, because
    /// nothing here can resolve "and what about that bit?". What it can do is the thing that matters
    /// most when no model is installed: put the right measured figure in front of someone who asked
    /// for it, in a sentence, instead of making them read a table. The half-dozen questions people
    /// actually ask an analysis are about its mode, its tempo, its range, its rhythm, its harmony and
    /// its hardest interval, and every one of those is a number this app already measured.</para>
    ///
    /// <para>Anything outside that set gets <see cref="QuestionPrompt.NotInDataMarker"/> rather than a
    /// vague paragraph. Declining is the honest answer and it is also the useful one: it tells the
    /// reader the deterministic writer is answering, and that installing a local model would get them
    /// further.</para>
    /// </summary>
    public Task<string> AnswerAsync(
        SongStats stats, IReadOnlyList<InterpretationTurn>? history, string question, CancellationToken ct)
        => Task.FromResult(Answer(stats, question));

    /// <summary>Topic keywords, most specific first — "chord tone" must beat bare "chord".</summary>
    private static readonly (string[] Words, Func<SongStats, string?> Write)[] Topics =
    [
        (["outside", "tension", "dissonan", "consonan", "chord tone", "against the chord"], TensionAnswer),
        (["why", "mode", "modal", "dorian", "lydian", "mixolydian", "aeolian", "ionian", "phrygian",
          "locrian", "pentatonic", "minor", "major", "key", "scale", "tonic"], ModeAnswer),
        (["tempo", "bpm", "fast", "slow", "speed", "pace", "drift", "steady"], TempoAnswer),
        (["range", "tessitura", "high", "low", "sing", "singer", "vocal", "voice", "octave"], RangeAnswer),
        (["rhythm", "syncopat", "beat", "groove", "off-beat", "offbeat", "note length", "swing"], RhythmAnswer),
        (["chord", "harmony", "harmonic", "progression", "changes"], HarmonyAnswer),
        (["leap", "interval", "jump", "step", "motion", "contour", "shape", "melod"], MotionAnswer),
        (["phrase", "breath", "section", "line length"], PhraseAnswer),
    ];

    private static string Answer(SongStats stats, string question)
    {
        var asked = question.ToLowerInvariant();
        foreach (var (words, write) in Topics)
        {
            if (words.Any(word => asked.Contains(word, StringComparison.Ordinal))
                && write(stats) is { Length: > 0 } answer)
            {
                return answer;
            }
        }

        return QuestionPrompt.NotInDataMarker + "\nThis answer was written without a language model, so "
            + "it can only report the measurements directly: the mode and the evidence for it, tempo, "
            + "vocal range, rhythm, harmony, melodic motion and phrasing. Install a local model with "
            + "'ollama pull llama3.2' to ask anything else.";
    }

    /// <summary>
    /// The "why this mode and not its neighbour" answer — the question this app exists to settle, and
    /// the one whose evidence is already fully computed in the vote tally.
    /// </summary>
    private static string? ModeAnswer(SongStats stats)
    {
        if (stats.PrimaryMode is null || stats.ModeVotes.Count == 0)
        {
            return $"No mode was settled on for this song. The tonic measured as {stats.TonicName}, but "
                + "no scale won enough windows to be called the song's mode.";
        }

        var winner = stats.ModeVotes[0];
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"It measured as {stats.TonicName} {stats.PrimaryMode}, which won "
            + $"{Round(winner.WindowPercent, 0)}% of the scored windows at a mean confidence of "
            + $"{Round(winner.AverageConfidence, 2)}.");

        if (winner.CharacteristicDegrees.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" The degree that names it is the {string.Join(" and ", winner.CharacteristicDegrees)}");
            var sung = stats.ScaleDegrees
                .Where(degree => degree.IsCharacteristic && degree.NoteCount > 0)
                .ToArray();
            text.Append(sung.Length > 0
                ? $", and the melody sings it: {string.Join(", ", sung.Select(degree => $"{degree.NoteName} on {Round(degree.Percent, 1)}% of notes"))}."
                : ", and the melody never sings it — so the reading rests on the harmony rather than on the vocal line.");
        }

        if (stats.ModeVotes.Count > 1)
        {
            var runner = stats.ModeVotes[1];
            text.Append(CultureInfo.InvariantCulture,
                $" The nearest alternative reading was {runner.Mode} at {Round(runner.WindowPercent, 0)}%.");
        }

        text.Append(CultureInfo.InvariantCulture,
            $" {Round(stats.InScalePercent, 1)}% of the melody's notes fall inside that scale.");
        return text.ToString();
    }

    private static string? TempoAnswer(SongStats stats)
    {
        if (stats.TempoBpm <= 0)
        {
            return "No tempo could be measured for this song — no reliable beat grid was found.";
        }

        var measured = stats.TempoEstimated
            ? ", estimated rather than counted from a clear grid."
            : ".";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"It runs at about {Round(stats.TempoBpm, 0)} BPM{measured}");

        if (stats.TempoMap is { Measures.Count: > 1 } map)
        {
            if (map.IsSteady)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $" It holds that tempo throughout — {Round(map.MinBpm, 0)} to {Round(map.MaxBpm, 0)} "
                    + $"BPM across {map.Measures.Count} measures is noise around one number.");
            }
            else
            {
                text.Append(CultureInfo.InvariantCulture,
                    $" It does not hold steady: the tempo moves between {Round(map.MinBpm, 0)} and "
                    + $"{Round(map.MaxBpm, 0)} BPM across {map.Measures.Count} measures, with a median "
                    + $"of {Round(map.MedianBpm, 0)}.");
            }
        }

        text.Append(CultureInfo.InvariantCulture,
            $" The melody moves at {Round(stats.NotesPerSecond, 2)} notes per second over "
            + $"{Round(stats.DurationSec, 0)} seconds.");
        return text.ToString();
    }

    private static string? RangeAnswer(SongStats stats)
    {
        if (stats.Tessitura is not { } voice)
        {
            return "No vocal range was measured — there is no melody line in this analysis to measure one from.";
        }

        var width = voice.SpanSemitones switch
        {
            <= 7 => "a narrow band most voices can cover",
            <= 14 => "a comfortable working range",
            _ => "wide enough to expose any weak part of a voice",
        };
        return new StringBuilder()
            .Append(CultureInfo.InvariantCulture,
                $"The line lives between {voice.LowLabel} and {voice.HighLabel} — that is the 10th to ")
            .Append("90th percentile of sung pitches, so a single showpiece note cannot widen it — ")
            .Append(CultureInfo.InvariantCulture,
                $"around a median of {voice.MedianLabel}. That is a span of {voice.SpanSemitones} ")
            .Append(CultureInfo.InvariantCulture, $"semitones, {width}.")
            .ToString();
    }

    private static string? RhythmAnswer(SongStats stats)
    {
        if (!stats.Rhythm.BeatGridUsable)
        {
            return "No reliable beat grid was found for this song, so where the notes sit against the "
                + "beat cannot be measured. Every rhythm figure is missing for that reason rather than "
                + "being zero.";
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"Onsets land {Round(stats.Rhythm.OnBeatPercent, 1)}% on the beat and "
            + $"{Round(stats.Rhythm.SyncopationPercent, 1)}% on the off-beat, which is "
            + $"{(stats.Rhythm.SyncopationPercent >= 25 ? "a melody pushing hard against the pulse" : stats.Rhythm.SyncopationPercent >= 10 ? "a melody playing with the pulse without fighting it" : "a melody sitting square on the pulse")}.");

        if (stats.Rhythm.NoteValues.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" By length the notes are {string.Join(", ", stats.Rhythm.NoteValues.Select(bucket => $"{bucket.Label} {Round(bucket.Percent, 0)}%"))}.");
        }
        return text.ToString();
    }

    private static string? HarmonyAnswer(SongStats stats)
    {
        if (stats.ChordVocabulary.UniqueChords == 0)
        {
            return "No chords were detected in this song, so there is no harmony to describe.";
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"There are {stats.ChordVocabulary.UniqueChords} distinct chords.");
        if (stats.ChordVocabulary.TopChords.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" The most used are {string.Join(", ", stats.ChordVocabulary.TopChords.Select(chord => $"{chord.Symbol} ({Round(chord.Percent, 0)}%)"))}.");
        }
        if (stats.ChordVocabulary.TopMoves.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" The commonest move is {stats.ChordVocabulary.TopMoves[0].From} to {stats.ChordVocabulary.TopMoves[0].To}, {stats.ChordVocabulary.TopMoves[0].Count} times.");
        }
        var turnover = stats.HarmonicRhythm.BeatGridUsable
            ? $"{Round(stats.HarmonicRhythm.AverageChordBeats, 2)} beats"
            : $"{Round(stats.HarmonicRhythm.AverageChordSec, 2)} seconds";
        text.Append(CultureInfo.InvariantCulture, $" The harmony turns over every {turnover}.");
        return text.ToString();
    }

    private static string? MotionAnswer(SongStats stats)
    {
        if (stats.Motion.IntervalCount == 0)
        {
            return "There are too few melody notes in this analysis to measure how the line moves.";
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"The line moves by step {Round(stats.Motion.StepPercent, 1)}% of the time, leaps a minor "
            + $"third or wider {Round(stats.Motion.LeapPercent, 1)}%, and repeats a pitch "
            + $"{Round(stats.Motion.RepeatPercent, 1)}%. The average interval is "
            + $"{Round(stats.Motion.AverageIntervalSemitones, 2)} semitones.");

        if (stats.BiggestLeap is { } leap)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" The widest jump is {leap.Semitones} semitones {(leap.Ascending ? "up" : "down")}, "
                + $"{leap.FromLabel} to {leap.ToLabel}, at {Round(leap.AtSec, 1)}s.");
        }

        text.Append(CultureInfo.InvariantCulture,
            $" Overall the contour is {stats.Contour.Shape.ToLowerInvariant()}, with "
            + $"{Round(stats.Contour.RisingPercent, 1)}% of moves rising.");
        return text.ToString();
    }

    private static string? PhraseAnswer(SongStats stats)
    {
        if (stats.Phrases.Count == 0)
        {
            return "No phrases were found — the melody has no rests long enough to split it on.";
        }

        return new StringBuilder()
            .Append(CultureInfo.InvariantCulture,
                $"There are {stats.Phrases.Count} phrases, found by splitting the melody wherever the ")
            .Append(CultureInfo.InvariantCulture,
                $"singer rests. They average {Round(stats.Phrases.AverageSec, 1)}s and ")
            .Append(CultureInfo.InvariantCulture,
                $"{Round(stats.Phrases.AverageNotes, 1)} notes. The longest runs ")
            .Append(CultureInfo.InvariantCulture,
                $"{Round(stats.Phrases.LongestSec, 1)}s starting at {Round(stats.Phrases.LongestStartSec, 1)}s")
            .Append(stats.Phrases.LongestSec >= 8 ? ", which will need planned breath." : ".")
            .ToString();
    }

    private static string? TensionAnswer(SongStats stats)
    {
        if (stats.ChordTones.ClassifiedNotes == 0)
        {
            return "No melody note could be matched to a sounding chord, so how the line sits against "
                + "the harmony was not measured. Notes over silence or an unrecognised symbol are "
                + "excluded rather than counted as tension.";
        }

        var balance = stats.ChordTones.ConsonancePercent switch
        {
            >= 70 => "stays consonant throughout",
            >= 45 => "balances landing points against passing colour",
            _ => "leans on tension over resolution",
        };
        return new StringBuilder()
            .Append("Against the chord underneath it the melody lands on the root ")
            .Append(CultureInfo.InvariantCulture,
                $"{Round(stats.ChordTones.RootPercent, 1)}% of the time, the third ")
            .Append(CultureInfo.InvariantCulture,
                $"{Round(stats.ChordTones.ThirdPercent, 1)}%, the fifth ")
            .Append(CultureInfo.InvariantCulture,
                $"{Round(stats.ChordTones.FifthPercent, 1)}% and the seventh ")
            .Append(CultureInfo.InvariantCulture,
                $"{Round(stats.ChordTones.SeventhPercent, 1)}%, leaving ")
            .Append(CultureInfo.InvariantCulture,
                $"{Round(stats.ChordTones.TensionPercent, 1)}% as other tension. At ")
            .Append(CultureInfo.InvariantCulture,
                $"{Round(stats.ChordTones.ConsonancePercent, 0)}% chord tones overall it {balance}.")
            .ToString();
    }

    private static string Write(SongStats stats)
    {
        if (stats.MelodyNoteCount == 0)
        {
            return stats.Fingerprint;
        }

        var text = new StringBuilder();
        text.Append(stats.Fingerprint);
        text.Append("\n\n");
        text.Append(SingerParagraph(stats));
        text.Append("\n\n");
        text.Append(CharacterParagraph(stats));

        // The same delimiter the LLM prompt asks for, so the selector splits every interpreter's
        // output the same way and the UI never has to know which one wrote it.
        text.Append("\n\n");
        text.Append(InterpretationPrompt.Delimiter);
        text.Append("\n\n");
        text.Append(ModalParagraph(stats));
        text.Append("\n\n");
        text.Append(HarmonyParagraph(stats));
        return text.ToString();
    }

    /// <summary>
    /// The theory half: what the modal engine decided and on what evidence. Written in proper terms
    /// on purpose — this section is only ever shown to a reader who asked for it.
    /// </summary>
    private static string ModalParagraph(SongStats stats)
    {
        if (stats.ModeVotes.Count == 0)
        {
            return "The modal engine found no window with sufficient evidence, so no mode is claimed.";
        }

        var text = new StringBuilder();
        var winner = stats.ModeVotes[0];
        text.Append(CultureInfo.InvariantCulture,
            $"Modal evidence: {winner.Mode} took {Round(winner.WindowPercent, 0)}% of the decided "
            + $"windows at a mean confidence of {Round(winner.AverageConfidence, 2)}");

        if (winner.CharacteristicDegrees.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $", identified by its {string.Join(" and ", winner.CharacteristicDegrees)}");
        }
        text.Append('.');

        var sung = stats.ScaleDegrees
            .Where(degree => degree.IsCharacteristic && degree.NoteCount > 0)
            .ToArray();
        if (sung.Length > 0)
        {
            var spelled = string.Join(" and ", sung.Select(degree =>
                $"the {degree.DegreeLabel} ({degree.NoteName}, {Round(degree.Percent, 1)}%)"));
            text.Append(CultureInfo.InvariantCulture, $" The melody sings {spelled}, so the ");
            text.Append("characteristic degrees are present rather than merely assumed.");
        }
        else if (winner.CharacteristicDegrees.Count > 0)
        {
            // Worth stating plainly: the mode was named on window scoring the sung line never confirms.
            text.Append(" The melody never sings those degrees, so the reading rests on the harmony "
                + "rather than on the vocal.");
        }

        if (stats.ModeVotes.Count > 1)
        {
            var runner = stats.ModeVotes[1];
            text.Append(CultureInfo.InvariantCulture,
                $" The nearest rival is {runner.Mode} at {Round(runner.WindowPercent, 0)}%");
            text.Append(stats.ModulationCount > 0
                ? $", across {stats.ModulationCount} mode changes."
                : ".");
        }

        return text.ToString();
    }

    /// <summary>The theory half, part two: melody against harmony, quantified.</summary>
    private static string HarmonyParagraph(SongStats stats)
    {
        var text = new StringBuilder();

        if (stats.ChordTones.ClassifiedNotes > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Against the sounding chord the line lands on the root {Round(stats.ChordTones.RootPercent, 1)}%, "
                + $"the third {Round(stats.ChordTones.ThirdPercent, 1)}%, "
                + $"the fifth {Round(stats.ChordTones.FifthPercent, 1)}% and "
                + $"the seventh {Round(stats.ChordTones.SeventhPercent, 1)}%, leaving "
                + $"{Round(stats.ChordTones.TensionPercent, 1)}% as other tension. ");
        }

        if (stats.HarmonicRhythm is { BeatGridUsable: true, AverageChordBeats: > 0 })
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Harmonic rhythm averages {Round(stats.HarmonicRhythm.AverageChordBeats, 2)} beats per "
                + $"chord over {stats.ChordVocabulary.UniqueChords} distinct symbols. ");
        }

        if (stats.Rhythm.BeatGridUsable)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Onsets sit {Round(stats.Rhythm.OnBeatPercent, 1)}% on the beat and "
                + $"{Round(stats.Rhythm.SyncopationPercent, 1)}% on the off-beat. ");
        }

        if (stats.Tessitura is { } voice)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"Tessitura runs {voice.LowLabel}-{voice.HighLabel} about a median {voice.MedianLabel}, "
                + $"with {Round(stats.Motion.LeapPercent, 1)}% of intervals a leap of a minor third or wider.");
        }

        return text.Length == 0
            ? "No chord or beat data was available, so no harmonic reading is possible."
            : text.ToString().TrimEnd();
    }

    /// <summary>What the numbers mean for whoever has to sing it.</summary>
    private static string SingerParagraph(SongStats stats)
    {
        var text = new StringBuilder("For a singer: ");

        text.Append(stats.Motion switch
        {
            { StepPercent: >= 60 } => "the line moves mostly by step, so it is comparatively easy to pitch",
            { LeapPercent: >= 40 } => "the line leaps often, so intervals need real attention",
            { RepeatPercent: >= 40 } => "the line sits on repeated pitches, so the challenge is delivery rather than pitching",
            _ => "the line mixes steps and leaps in ordinary proportion",
        });

        if (stats.Tessitura is { } voice)
        {
            text.Append(CultureInfo.InvariantCulture,
                $". Most of the work sits between {voice.LowLabel} and {voice.HighLabel}");
            text.Append(voice.SpanSemitones switch
            {
                <= 7 => ", a narrow band that suits almost any voice",
                <= 14 => ", a comfortable working range",
                _ => ", a wide band that will expose any weak part of a voice",
            });
        }

        if (stats.Phrases.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $". Phrases average {Round(stats.Phrases.AverageSec, 1)}s");
            text.Append(stats.Phrases.LongestSec >= 8
                ? $", but the longest runs {Round(stats.Phrases.LongestSec, 1)}s and will need planned breath"
                : ", short enough to breathe naturally");
        }

        text.Append('.');
        return text.ToString();
    }

    /// <summary>What the harmony and rhythm together suggest about the song's character.</summary>
    private static string CharacterParagraph(SongStats stats)
    {
        var text = new StringBuilder("In character: ");

        text.Append(stats.ChordVocabulary.UniqueChords switch
        {
            0 => "no harmony was detected",
            <= 4 => $"the harmony is spare, just {stats.ChordVocabulary.UniqueChords} chords",
            <= 8 => $"the harmony is conventional in size, {stats.ChordVocabulary.UniqueChords} chords",
            _ => $"the harmony is rich, {stats.ChordVocabulary.UniqueChords} distinct chords",
        });

        if (stats.HarmonicRhythm is { BeatGridUsable: true, AverageChordBeats: > 0 } harmony)
        {
            text.Append(harmony.AverageChordBeats switch
            {
                < 2 => ", turning over quickly at under two beats each",
                <= 4 => ", moving at a steady bar-ish pace",
                _ => ", each one held long enough to settle",
            });
        }

        if (stats.Rhythm.BeatGridUsable)
        {
            text.Append(stats.Rhythm.SyncopationPercent switch
            {
                >= 25 => ". The melody pushes hard against the beat",
                >= 10 => ". The melody plays with the beat without fighting it",
                _ => ". The melody sits square on the beat",
            });
        }

        if (stats.ChordTones.ClassifiedNotes > 0)
        {
            text.Append(stats.ChordTones.ConsonancePercent switch
            {
                >= 70 => $", and at {Round(stats.ChordTones.ConsonancePercent, 0)}% chord tones it stays consonant throughout",
                >= 45 => $", and at {Round(stats.ChordTones.ConsonancePercent, 0)}% chord tones it balances landing points against passing colour",
                _ => $", and at only {Round(stats.ChordTones.ConsonancePercent, 0)}% chord tones it leans on tension over resolution",
            });
        }

        text.Append('.');
        return text.ToString();
    }

    private static string Round(double value, int digits)
        => Math.Round(value, digits).ToString($"F{digits}", CultureInfo.InvariantCulture);
}
