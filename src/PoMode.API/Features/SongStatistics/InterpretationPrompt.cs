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
    /// Written for someone who loves music but has never studied it. The fingerprint paragraph sits
    /// directly above this in the UI and already states every figure precisely, so repeating numbers
    /// here is both redundant and exactly what made the section a wall of percentages — hence the
    /// instruction to turn them into words. The grounding rules are unchanged: they are what stops a
    /// model naming an artist or a genre it thinks it recognises.
    /// </summary>
    public const string System =
        "You are explaining one song to a curious listener who loves music but has never studied "
        + "music theory. You will be given measurements taken from the song's audio. Your job is to "
        + "say what they mean for how the song sounds, how it feels, and what it would be like to sing.\n"
        + "\n"
        + "How to write:\n"
        + "- Warm, plain, everyday English. Short sentences.\n"
        + "- Avoid jargon. If you must use a musical term, explain it in ordinary words in the same "
        + "sentence.\n"
        + "- Use very few numbers. Pick the two or three that matter most and turn the rest into "
        + "words: 'almost every note', 'about half the time', 'now and then'.\n"
        + "- Never make the reader do arithmetic. 'Two thirds of the time' beats '66.7%'.\n"
        + "- Describe what a listener would actually hear, and what a singer would actually feel.\n"
        + "- Three short paragraphs. No headings, no bullet points, no markdown.\n"
        + "\n"
        + "Rules you must not break:\n"
        + "- Use ONLY the measurements provided. Never invent a statistic, a section, a lyric, a "
        + "genre presented as fact, an artist, or a song title.\n"
        + "- If something is not in the data, do not mention it.\n"
        + "- Do not list the measurements back. Say what they mean.\n"
        + "- Be confident. Do not hedge about data you were given.";

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
