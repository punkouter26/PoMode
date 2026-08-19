using System.Globalization;
using System.Text;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Builds the one prompt every LLM interpreter sends, so the local and the cloud model are asked
/// exactly the same question and their answers stay comparable.
///
/// <para>The user message is a flat list of measured facts and nothing else — no audio, no title, no
/// lyrics, no artist. A model cannot report what it was never given, so the most likely failure mode
/// (confidently naming a genre or an artist it "recognises") is designed out rather than instructed
/// away. The system message then forbids adding numbers, which is the remaining risk.</para>
/// </summary>
public static class InterpretationPrompt
{
    /// <summary>
    /// The line an interpreter puts between its two audiences. Distinctive enough that no ordinary
    /// prose produces it by accident, so <see cref="Split"/> can never cut a paragraph in half.
    /// </summary>
    public const string Delimiter = "===FOR MUSICIANS===";

    /// <summary>
    /// Asks for both audiences in a single response.
    ///
    /// <para>One call, not two, and the reason is latency: a local model takes tens of seconds, and
    /// two calls would double a wait the user is already sitting through. Writing both halves at once
    /// also keeps them consistent — the theory section explains the same reading of the song the plain
    /// section gave, rather than a second independent take on it.</para>
    ///
    /// <para>The grounding rules apply to both halves and are what stop a model naming an artist or a
    /// genre it thinks it recognises. The plain half additionally forbids numbers, because the
    /// fingerprint paragraph directly above it in the UI already states every figure exactly.</para>
    /// </summary>
    public const string System =
        "You will be given measurements taken from one song's audio. Write TWO summaries of it, one "
        + "after the other, separated by a line containing only " + Delimiter + "\n"
        + "\n"
        + "PART 1 — for a curious listener who loves music but has never studied music theory.\n"
        + "- Say what the measurements mean for how the song sounds, how it feels, and what it would "
        + "be like to sing.\n"
        + "- Warm, plain, everyday English. Short sentences.\n"
        + "- Avoid jargon. If you must use a musical term, explain it in ordinary words in the same "
        + "sentence.\n"
        + "- Use very few numbers. Pick the two or three that matter most and turn the rest into "
        + "words: 'almost every note', 'about half the time', 'now and then'.\n"
        + "- Never make the reader do arithmetic. 'Two thirds of the time' beats '66.7%'.\n"
        + "- Three short paragraphs.\n"
        + "\n"
        + "PART 2 — for a trained musician.\n"
        + "- Assume full command of theory. Use the proper terms without explaining them: mode, "
        + "characteristic degree, tessitura, harmonic rhythm, chord tone, syncopation.\n"
        + "- Be precise and quantitative here. Cite the actual figures.\n"
        + "- Discuss the modal evidence: which degrees the melody emphasises, whether they support the "
        + "named mode, and what the mode changes imply.\n"
        + "- Discuss how the melody sits against the harmony — which chord degrees it lands on, and "
        + "what the tension proportion means for the writing.\n"
        + "- Note anything a musician would find unusual or contradictory in the data.\n"
        + "- Two or three paragraphs.\n"
        + "\n"
        + "Rules you must not break, in BOTH parts:\n"
        + "- Use ONLY the measurements provided. Never invent a statistic, a section, a lyric, a "
        + "genre presented as fact, an artist, or a song title.\n"
        + "- If something is not in the data, do not mention it.\n"
        + "- Do not list the measurements back. Say what they mean.\n"
        + "- Be confident. Do not hedge about data you were given.\n"
        + "- Plain prose only. No headings, no bullet points, no markdown, no part labels.";

    /// <summary>
    /// Splits a raw interpretation into its plain-English and theory halves at the first delimiter
    /// line.
    ///
    /// <para>Matching is deliberately tolerant rather than exact. A model asked to reproduce a
    /// literal token sometimes mangles it — gemma4:26b emitted <c>===FOR MUSICIating===</c>, having
    /// written both halves perfectly — and an exact match would have thrown the entire theory
    /// section into the plain summary, delimiter and all, in front of the reader. So a line counts
    /// as the delimiter when its letters begin "FOR MUSIC", provided it is punctuated or short
    /// enough to be a marker rather than a sentence that happens to open "For musicians, ...".</para>
    ///
    /// <para>A response with no delimiter at all is treated as a single plain-English summary and the
    /// theory half comes back null; the UI then omits that section. Everything after the first
    /// delimiter is the theory half, so a repeated marker cannot fragment the output.</para>
    /// </summary>
    public static (string Plain, string? Theory) Split(string raw)
    {
        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsDelimiterLine(lines[i]))
            {
                continue;
            }

            var plain = string.Join("\n", lines[..i]).Trim();
            var theory = string.Join("\n", lines[(i + 1)..]).Trim();

            // A delimiter with nothing on one side of it is not two summaries.
            return (plain.Length == 0 ? theory : plain,
                plain.Length == 0 || theory.Length == 0 ? null : theory);
        }

        return (raw.Trim(), null);
    }

    /// <summary>Longest a delimiter line can be before it is more plausibly a sentence.</summary>
    private const int MaxDelimiterLength = 40;

    /// <summary>Rule characters a model might use to draw a separator on its own line.</summary>
    private static readonly char[] RuleCharacters = ['=', '-', '_', '*', '#'];

    private static bool IsDelimiterLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        // A line of nothing but rule characters is a separator and cannot be anything else — the
        // prompt forbids markdown, so no horizontal rule belongs in the prose. gpt-5.4-nano wrote a
        // bare "===" where the full marker was asked for, having produced both halves correctly;
        // without this the entire theory section, delimiter included, reached the reader as one blob.
        if (trimmed.Length >= 3 && trimmed.All(character => RuleCharacters.Contains(character)))
        {
            return true;
        }

        var letters = string.Concat(trimmed.Where(char.IsLetter)).ToUpperInvariant();
        if (!letters.StartsWith("FORMUSIC", StringComparison.Ordinal))
        {
            return false;
        }

        // Either it is decorated like a marker, or it is too short to be prose. Both guards exist so
        // a theory paragraph opening "For musicians the notable feature is ..." is never eaten.
        return trimmed.Contains('=', StringComparison.Ordinal)
            || trimmed.Length <= MaxDelimiterLength;
    }

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
