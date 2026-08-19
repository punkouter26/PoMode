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
        return text.ToString();
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
