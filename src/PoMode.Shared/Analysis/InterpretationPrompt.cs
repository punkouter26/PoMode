using System.Globalization;
using System.Text;

namespace PoMode.Shared.Analysis;

/// <summary>
/// Builds the one prompt every LLM interpreter is sent — Ollama on the server and the model built
/// into the browser alike — so their answers stay comparable. In Shared rather than the API for that
/// second reader: the browser tier builds the same prompt from the <see cref="SongStats"/> it already
/// holds, instead of asking the server for it.
///
/// <para>The user message is a flat list of measured facts and nothing else — no audio, no title, no
/// lyrics, no artist. A model cannot report what it was never given, so the most likely failure mode
/// (confidently naming a genre or an artist it "recognises") is designed out rather than instructed
/// away. The system message then forbids adding numbers, and <see cref="GroundingCheck"/> catches the
/// ones a model adds anyway.</para>
/// </summary>
public static class InterpretationPrompt
{
    public const string PlainField = "plain";
    public const string TheoryField = "theory";

    /// <summary>
    /// The reply's shape, handed to the runtime as a constraint rather than described in prose. This
    /// replaced a delimiter line between the two halves, which models reproduced as
    /// <c>===FOR MUSICIating===</c> or a bare <c>===</c> often enough to need a tolerant parser of its
    /// own; a schema-constrained decoder cannot write anything but the two fields.
    /// </summary>
    public const string Schema =
        """{"type":"object","properties":{"plain":{"type":"string"},"theory":{"type":"string"}},"required":["plain","theory"]}""";

    /// <summary>
    /// The one system message every request about a song is sent — the summary and every question
    /// alike — carrying only the rules that hold for all of them.
    ///
    /// <para>Shared, and the measurements lead the user message with the task after them, for
    /// latency: Ollama (llama.cpp) reuses the processed prefix of its previous request, so once any
    /// request about a song has run, the next one only processes what follows the statistics. With a
    /// system prompt per task the prefixes never matched, and on a CPU every question paid the whole
    /// ~1,200-token statistics block again — close to a minute before its first word.</para>
    ///
    /// <para>The rules are what stop a model naming an artist or a genre it thinks it recognises,
    /// and what <see cref="GroundingCheck"/> then enforces for figures.</para>
    /// </summary>
    public const string System =
        "You write about one song using only measurements taken from its audio. The measurements come "
        + "first, then what to write.\n"
        + "\n"
        + "Rules you must not break:\n"
        + "- Use ONLY the measurements provided, and what general music theory says about them. Never "
        + "invent a statistic, a section, a lyric, a genre presented as fact, an artist, or a song title "
        + "- you were not given any of those and cannot know them.\n"
        + "- Never compute a new figure from the given ones: no sums, differences, ratios or unit "
        + "conversions. Quote a figure exactly as given or put it in words.\n"
        + "- If something is not in the data, do not mention it. Do not list the measurements back: "
        + "say what they mean.\n"
        + "- Be confident. Do not hedge about data you were given.\n"
        + "- Plain prose only. No headings, no bullet points, no markdown.";

    /// <summary>
    /// What the summary asks for: both audiences in a single response. One call, not two, and the
    /// reason is latency — a local model takes tens of seconds, and two calls would double a wait
    /// the user is already sitting through. Writing both halves at once also keeps them consistent.
    /// The plain half forbids numbers, because the fingerprint paragraph directly above it in the UI
    /// already states every figure exactly.
    /// </summary>
    public const string Task =
        "Write TWO summaries of this song and reply with a JSON object: \"plain\" holds PART 1 and "
        + "\"theory\" holds PART 2. Inside each, separate paragraphs with a blank line; no part labels.\n"
        + "\n"
        + "PART 1 - for a curious listener who loves music but has never studied music theory.\n"
        + "- Say what the measurements mean for how the song sounds, how it feels, and what it would "
        + "be like to sing.\n"
        + "- Warm, plain, everyday English. Short sentences.\n"
        + "- Avoid jargon. If you must use a musical term, explain it in ordinary words in the same "
        + "sentence.\n"
        + "- Use very few numbers. Pick the two or three that matter most and turn the rest into "
        + "words: 'almost every note', 'about half the time', 'now and then'.\n"
        + "- Never make the reader do arithmetic. 'Two thirds of the time' beats a percentage.\n"
        + "- Three short paragraphs.\n"
        + "\n"
        + "PART 2 - for a trained musician.\n"
        + "- Assume full command of theory. Use the proper terms without explaining them: mode, "
        + "characteristic degree, tessitura, harmonic rhythm, chord tone, syncopation.\n"
        + "- Be precise and quantitative here. Cite the actual figures exactly as given.\n"
        + "- Discuss the modal evidence: which degrees the melody emphasises, whether they support the "
        + "named mode, and what the mode changes imply.\n"
        + "- Discuss how the melody sits against the harmony - which chord degrees it lands on, and "
        + "what the tension proportion means for the writing.\n"
        + "- Note anything a musician would find unusual or contradictory in the data.\n"
        + "- Two or three paragraphs.";

    /// <summary>The whole request for a summary of <paramref name="stats"/>: the statistics, then the task.</summary>
    public static ChatPrompt For(SongStats stats)
        => new(System, [new ChatMessage("user", $"{User(stats)}\n{Task}")], Schema);

    /// <summary>The measured facts, one per line. Deliberately terse — this is data, not prose.</summary>
    public static string User(SongStats stats)
    {
        var text = new StringBuilder();
        text.AppendLine("Here are the measured statistics for one song.");
        text.AppendLine();

        Add(text, "Key / mode", stats.PrimaryMode is null
            ? $"{stats.TonicName}, mode undetermined"
            : $"{stats.TonicName} {stats.PrimaryMode} (confidence {N(stats.PrimaryConfidence * 100, 0)}%)");
        Add(text, "Tempo", stats.TempoBpm > 0
            ? $"{N(stats.TempoBpm, 0)} BPM{(stats.TempoEstimated ? " (estimated)" : "")}"
            : "not determined");
        if (stats.TempoMap is { Measures.Count: > 1 } map)
        {
            Add(text, "Tempo steadiness", map.IsSteady
                ? $"steady throughout ({N(map.MinBpm, 0)}-{N(map.MaxBpm, 0)} BPM across "
                    + $"{map.Measures.Count} measures)"
                : $"drifts — {N(map.MinBpm, 0)} to {N(map.MaxBpm, 0)} BPM across {map.Measures.Count} "
                    + $"measures, median {N(map.MedianBpm, 0)}");
        }

        Add(text, "Duration", $"{N(stats.DurationSec, 0)} seconds");
        Add(text, "Melody notes", $"{stats.MelodyNoteCount} ({N(stats.NotesPerSecond, 2)} per second, "
            + $"average length {N(stats.AverageNoteSec, 2)}s)");
        Add(text, "Notes inside the key", $"{N(stats.InScalePercent, 1)}%");
        Add(text, "Mode changes across the song", stats.ModulationCount.ToString(CultureInfo.InvariantCulture));

        if (stats.Tessitura is { } voice)
        {
            Add(text, "Vocal tessitura", $"median {voice.MedianLabel}, "
                + $"10th-90th percentile {voice.LowLabel} to {voice.HighLabel} ({voice.SpanSemitones} semitones)");
        }

        Add(text, "Melodic motion", $"{N(stats.Motion.StepPercent, 1)}% steps (1-2 semitones), "
            + $"{N(stats.Motion.LeapPercent, 1)}% leaps (3+), "
            + $"{N(stats.Motion.RepeatPercent, 1)}% repeated pitches, "
            + $"average interval {N(stats.Motion.AverageIntervalSemitones, 2)} semitones");

        if (stats.BiggestLeap is { } leap)
        {
            Add(text, "Widest leap", $"{leap.Semitones} semitones {(leap.Ascending ? "up" : "down")} "
                + $"({leap.FromLabel} to {leap.ToLabel}) at {N(leap.AtSec, 1)}s");
        }

        // Deliberately omits ContourProfile.NetSemitones. Shape and net answer the same question two
        // ways — Shape from the average of the outer thirds, net from literally the first and last
        // note — so they can honestly disagree ("falling, yet ends 4 semitones higher"). A reader
        // seeing both columns copes; a model asked for flowing prose spends a sentence reconciling
        // them. The figure is still served by /stats and shown in the Advanced panel.
        Add(text, "Contour", $"{stats.Contour.Shape}; {N(stats.Contour.RisingPercent, 1)}% of moves rise");

        if (stats.Rhythm.BeatGridUsable)
        {
            Add(text, "Onset placement", $"{N(stats.Rhythm.OnBeatPercent, 1)}% on the beat, "
                + $"{N(stats.Rhythm.SyncopationPercent, 1)}% on the off-beat");
            if (stats.Rhythm.NoteValues.Count > 0)
            {
                Add(text, "Note lengths", string.Join(", ", stats.Rhythm.NoteValues
                    .Select(bucket => $"{bucket.Label} {N(bucket.Percent, 0)}%")));
            }
        }
        else
        {
            Add(text, "Rhythm", "no reliable beat grid was found, so onset placement is unknown");
        }

        Add(text, "Phrases", $"{stats.Phrases.Count}, averaging {N(stats.Phrases.AverageSec, 1)}s and "
            + $"{N(stats.Phrases.AverageNotes, 1)} notes; longest {N(stats.Phrases.LongestSec, 1)}s "
            + $"starting at {N(stats.Phrases.LongestStartSec, 1)}s");

        Add(text, "Chord vocabulary", $"{stats.ChordVocabulary.UniqueChords} distinct chords"
            + (stats.ChordVocabulary.TopChords.Count == 0
                ? ""
                : "; most used " + string.Join(", ", stats.ChordVocabulary.TopChords
                    .Select(chord => $"{chord.Symbol} ({N(chord.Percent, 0)}%)"))));

        if (stats.ChordVocabulary.TopMoves.Count > 0)
        {
            Add(text, "Commonest chord moves", string.Join(", ", stats.ChordVocabulary.TopMoves
                .Select(move => $"{move.From} to {move.To} x{move.Count}")));
        }

        Add(text, "Harmonic rhythm", stats.HarmonicRhythm.BeatGridUsable
            ? $"a chord every {N(stats.HarmonicRhythm.AverageChordBeats, 2)} beats "
                + $"({N(stats.HarmonicRhythm.AverageChordSec, 2)}s)"
            : $"a chord every {N(stats.HarmonicRhythm.AverageChordSec, 2)}s");

        if (stats.ChordTones.ClassifiedNotes > 0)
        {
            Add(text, "Melody against the chord", $"root {N(stats.ChordTones.RootPercent, 1)}%, "
                + $"third {N(stats.ChordTones.ThirdPercent, 1)}%, "
                + $"fifth {N(stats.ChordTones.FifthPercent, 1)}%, "
                + $"seventh {N(stats.ChordTones.SeventhPercent, 1)}%, "
                + $"other tension {N(stats.ChordTones.TensionPercent, 1)}%");
        }

        text.AppendLine();
        text.AppendLine("A factual summary of the same data reads:");
        text.AppendLine(stats.Fingerprint);
        return text.ToString();
    }

    private static void Add(StringBuilder text, string label, string value)
        => text.AppendLine(CultureInfo.InvariantCulture, $"- {label}: {value}");

    /// <summary>Invariant formatting: a comma decimal separator would read as two numbers to a model.</summary>
    private static string N(double value, int digits)
        => Math.Round(value, digits).ToString($"F{digits}", CultureInfo.InvariantCulture);
}
