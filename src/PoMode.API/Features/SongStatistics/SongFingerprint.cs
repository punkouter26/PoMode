using System.Globalization;
using System.Text;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Writes the computed statistics out as one plain-English paragraph — the "what is this song"
/// summary that both views show and that the LLM interpreters are handed as their only source of
/// fact.
///
/// <para>Two rules govern every clause here. First, <b>a weak number is omitted, never hedged</b>:
/// when the tonic, the mode or the beat grid was not confidently found, the sentence that would have
/// used it simply does not appear. A short confident paragraph is more useful than a long one full of
/// "possibly". Second, <b>no clause invents anything</b> — every figure traces to a field on
/// <see cref="SongStats"/>.</para>
/// </summary>
public static class SongFingerprint
{
    /// <summary>Below this the modal engine's own pick is not worth stating as fact.</summary>
    private const double MinModeConfidence = 0.5;

    /// <summary>A leap this wide or wider is remarkable enough to name in a summary.</summary>
    private const int NotableLeapSemitones = 7;

    public static string Write(SongStats stats)
    {
        if (stats.MelodyNoteCount == 0)
        {
            return "No melody was transcribed for this song, so there is nothing to summarise yet.";
        }

        var sentences = new List<string>
        {
            Opening(stats),
        };

        AddIfPresent(sentences, VoiceSentence(stats));
        AddIfPresent(sentences, MotionSentence(stats));
        AddIfPresent(sentences, RhythmSentence(stats));
        AddIfPresent(sentences, PhraseSentence(stats));
        AddIfPresent(sentences, HarmonySentence(stats));
        AddIfPresent(sentences, ColourSentence(stats));

        return string.Join(" ", sentences);
    }

    private static void AddIfPresent(List<string> sentences, string? sentence)
    {
        if (sentence is not null)
        {
            sentences.Add(sentence);
        }
    }

    /// <summary>Key, mode and tempo — the one sentence that always appears.</summary>
    private static string Opening(SongStats stats)
    {
        var key = stats.PrimaryMode is { } mode && stats.PrimaryConfidence >= MinModeConfidence
            ? $"{stats.TonicName} {mode}"
            : $"{stats.TonicName} (mode unclear)";

        // A drifting tempo is worth a clause: "at 96 BPM" is misleading for a performance that
        // ranged from 91 to 104, and the range is the more honest headline.
        var tempo = stats.TempoBpm switch
        {
            > 0 when stats.TempoMap is { IsSteady: false, Measures.Count: > 1 } map =>
                $" at around {Round(map.MedianBpm, 0)} BPM, drifting between "
                + $"{Round(map.MinBpm, 0)} and {Round(map.MaxBpm, 0)}",
            > 0 => $" at {Round(stats.TempoBpm, 0)} BPM{(stats.TempoEstimated ? " (estimated)" : "")}",
            _ => "",
        };

        return $"This song is in {key}{tempo}, running {Duration(stats.DurationSec)}.";
    }

    /// <summary>Where the voice lives, which is more useful than where it can reach.</summary>
    private static string? VoiceSentence(SongStats stats)
    {
        if (stats.Tessitura is not { } voice)
        {
            return null;
        }

        return $"The vocal centres on {voice.MedianLabel} and spends most of its time between "
            + $"{voice.LowLabel} and {voice.HighLabel} ({voice.SpanSemitones} semitones), "
            + $"across {stats.MelodyNoteCount} notes.";
    }

    /// <summary>How the line moves, plus the one leap worth naming.</summary>
    private static string? MotionSentence(SongStats stats)
    {
        if (stats.Motion.IntervalCount == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"The melody is {Describe(stats.Motion)} — ");
        text.Append(CultureInfo.InvariantCulture, $"{Round(stats.Motion.StepPercent, 0)}% steps, ");
        text.Append(CultureInfo.InvariantCulture, $"{Round(stats.Motion.LeapPercent, 0)}% leaps, ");
        text.Append(CultureInfo.InvariantCulture, $"{Round(stats.Motion.RepeatPercent, 0)}% repeated notes");

        if (stats.BiggestLeap is { } leap && leap.Semitones >= NotableLeapSemitones)
        {
            var direction = leap.Ascending ? "up" : "down";
            text.Append(CultureInfo.InvariantCulture,
                $", with its widest jump {direction} {leap.Semitones} semitones "
                + $"({leap.FromLabel} to {leap.ToLabel}) at {Clock(leap.AtSec)}");
        }

        text.Append(CultureInfo.InvariantCulture, $". Its overall shape is {stats.Contour.Shape.ToLowerInvariant()}.");
        return text.ToString();
    }

    private static string Describe(MotionProfile motion) => motion switch
    {
        { RepeatPercent: >= 40 } => "chant-like, hovering on repeated pitches",
        { StepPercent: >= 60 } => "mostly stepwise and easy to pitch",
        { LeapPercent: >= 40 } => "leap-heavy and angular",
        _ => "a mix of steps and leaps",
    };

    /// <summary>Omitted entirely when the beat grid was not usable — there is no honest version of it.</summary>
    private static string? RhythmSentence(SongStats stats)
    {
        if (!stats.Rhythm.BeatGridUsable)
        {
            return null;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"Rhythmically it is {Describe(stats.Rhythm)}: {Round(stats.Rhythm.OnBeatPercent, 0)}% of notes "
            + $"land on the beat and {Round(stats.Rhythm.SyncopationPercent, 0)}% land on the off-beat");

        if (stats.Rhythm.NoteValues.Count > 0)
        {
            var busiest = stats.Rhythm.NoteValues.MaxBy(bucket => bucket.Count)!;
            text.Append(CultureInfo.InvariantCulture,
                $", and the commonest note length is the {busiest.Label.ToLowerInvariant()} "
                + $"({Round(busiest.Percent, 0)}%)");
        }

        text.Append('.');
        return text.ToString();
    }

    private static string Describe(RhythmProfile rhythm) => rhythm switch
    {
        { SyncopationPercent: >= 25 } => "strongly syncopated",
        { OnBeatPercent: >= 60 } => "squarely on the grid",
        _ => "loosely placed",
    };

    private static string? PhraseSentence(SongStats stats)
    {
        if (stats.Phrases.Count == 0)
        {
            return null;
        }

        return $"It breaks into {Plural(stats.Phrases.Count, "phrase")} averaging "
            + $"{Round(stats.Phrases.AverageSec, 1)}s and {Round(stats.Phrases.AverageNotes, 0)} notes, "
            + $"the longest running {Round(stats.Phrases.LongestSec, 1)}s from {Clock(stats.Phrases.LongestStartSec)}.";
    }

    private static string? HarmonySentence(SongStats stats)
    {
        if (stats.ChordVocabulary.UniqueChords == 0)
        {
            return null;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"The harmony uses {Plural(stats.ChordVocabulary.UniqueChords, "distinct chord")}");

        if (stats.HarmonicRhythm is { BeatGridUsable: true, AverageChordBeats: > 0 } harmony)
        {
            text.Append(CultureInfo.InvariantCulture,
                $", changing about every {Round(harmony.AverageChordBeats, 1)} beats");
        }
        else if (stats.HarmonicRhythm.AverageChordSec > 0)
        {
            text.Append(CultureInfo.InvariantCulture,
                $", changing about every {Round(stats.HarmonicRhythm.AverageChordSec, 1)}s");
        }

        if (stats.ChordVocabulary.TopMoves.Count > 0)
        {
            var move = stats.ChordVocabulary.TopMoves[0];
            text.Append(CultureInfo.InvariantCulture, $", and its commonest move is {move.From} to {move.To}");
        }

        text.Append('.');
        return text.ToString();
    }

    /// <summary>How the melody sits against the harmony, and how often it steps outside the key.</summary>
    private static string? ColourSentence(SongStats stats)
    {
        var parts = new List<string>();

        if (stats.ChordTones.ClassifiedNotes > 0)
        {
            parts.Add($"{Round(stats.ChordTones.ConsonancePercent, 0)}% of the melody lands on a chord tone "
                + $"(most often the {StrongestDegree(stats.ChordTones)})");
        }

        parts.Add($"{Round(stats.InScalePercent, 0)}% stays inside the key");

        if (stats.ModulationCount > 0)
        {
            parts.Add($"the mode shifts {Plural(stats.ModulationCount, "time")} across the song");
        }

        return $"{Capitalise(string.Join(", ", parts))}.";
    }

    private static string StrongestDegree(ChordToneProfile tones)
    {
        var ranked = new (string Label, double Percent)[]
        {
            ("root", tones.RootPercent),
            ("third", tones.ThirdPercent),
            ("fifth", tones.FifthPercent),
        };
        return ranked.MaxBy(entry => entry.Percent).Label;
    }

    // ---- Formatting ------------------------------------------------------------------------

    /// <summary>
    /// Invariant culture throughout: this string is both UI copy and LLM prompt input, and a
    /// comma decimal separator would change "2.5 beats" into something a model reads as two values.
    /// </summary>
    private static string Round(double value, int digits)
        => Math.Round(value, digits).ToString($"F{digits}", CultureInfo.InvariantCulture);

    private static string Plural(int count, string noun)
        => $"{count} {noun}{(count == 1 ? "" : "s")}";

    private static string Clock(double seconds)
        => $"{(int)(seconds / 60)}:{(int)(seconds % 60):00}";

    private static string Duration(double seconds)
        => seconds >= 60
            ? $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s"
            : $"{Round(seconds, 0)}s";

    private static string Capitalise(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
