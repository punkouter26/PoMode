using System.Text;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Builds the prompt for a follow-up question about a song, so every interpreter is asked the same
/// way and their answers stay comparable — the same contract <see cref="InterpretationPrompt"/> holds
/// for the written summary.
///
/// <para>The grounding is identical and for the identical reason: the model is handed measurements
/// and nothing else, so the failure it cannot commit is the one that matters most here. A written
/// summary that drifts is merely florid; an <em>answer</em> that drifts is a claim made in response
/// to a direct question, which a reader has every reason to believe.</para>
///
/// <para>The one thing this prompt adds is permission to decline. A summary can always be written
/// from the data, but a question need not be answerable from it — "what are the lyrics about?" has no
/// answer in a list of intervals — and a model with no way to say so will invent one. So it is given
/// <see cref="NotInDataMarker"/> and told to use it, and the client labels an answer that carries it
/// rather than hiding the refusal.</para>
/// </summary>
public static class QuestionPrompt
{
    /// <summary>
    /// The line an interpreter opens with when the measurements do not cover the question. Distinctive
    /// enough that ordinary prose cannot produce it by accident, matching the summary delimiter's
    /// design.
    /// </summary>
    public const string NotInDataMarker = "NOT IN THE DATA";

    /// <summary>
    /// Exchanges carried into the prompt, newest kept. A local model's context is finite and the
    /// measurements are worth more than the eighth exchange, so the tail is what gets dropped from
    /// the front.
    /// </summary>
    public const int MaxHistoryTurns = 6;

    /// <summary>Longer than this and the "question" is a document. Truncated rather than refused,
    /// because a user who pastes a paragraph still deserves an answer to the first part of it.</summary>
    public const int MaxQuestionLength = 500;

    public const string System =
        "You are answering one question about a single song, using measurements taken from its audio.\n"
        + "\n"
        + "- Answer ONLY from the measurements you are given, and from what general music theory says "
        + "about those measurements.\n"
        + "- You may explain, compare and reason about the figures. You may not add new ones.\n"
        + "- Never name an artist, a song title, a genre-as-fact, a section, or a lyric. You were not "
        + "given any of those and cannot know them.\n"
        + "- If the measurements do not contain what was asked, reply with a first line of exactly "
        + NotInDataMarker + " and then one sentence saying what would be needed to answer it. Do this "
        + "rather than guessing.\n"
        + "- Match the questioner's level. If they use theory terms, use them back; if they do not, "
        + "explain in ordinary words.\n"
        + "- Be direct. Lead with the answer, then the evidence for it.\n"
        + "- One or two short paragraphs. Plain prose — no headings, no bullet points, no markdown.";

    /// <summary>
    /// The measurements, then the conversation so far, then the question.
    ///
    /// <para>The statistics come first and in full on every turn rather than only on the first. A
    /// model that has to recall a figure from six messages ago will approximate it, and an
    /// approximated statistic presented as measured is the exact failure this prompt exists to
    /// prevent.</para>
    /// </summary>
    public static string User(
        SongStats stats, IReadOnlyList<InterpretationTurn>? history, string question)
    {
        var text = new StringBuilder();
        text.AppendLine(InterpretationPrompt.User(stats));

        var recent = Recent(history);
        if (recent.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("The conversation so far:");
            foreach (var turn in recent)
            {
                text.AppendLine($"Q: {Clean(turn.Question)}");
                text.AppendLine($"A: {Clean(turn.Answer)}");
            }
        }

        text.AppendLine();
        text.AppendLine("The question to answer now:");
        text.AppendLine(Clean(question));
        return text.ToString();
    }

    /// <summary>The last <see cref="MaxHistoryTurns"/> exchanges, oldest first, empties dropped.</summary>
    public static IReadOnlyList<InterpretationTurn> Recent(IReadOnlyList<InterpretationTurn>? history)
    {
        if (history is null || history.Count == 0)
        {
            return [];
        }
        var usable = history
            .Where(turn => !string.IsNullOrWhiteSpace(turn.Question) && !string.IsNullOrWhiteSpace(turn.Answer))
            .ToList();
        return usable.Count <= MaxHistoryTurns
            ? usable
            : usable[^MaxHistoryTurns..];
    }

    /// <summary>
    /// Separates a raw answer from whether it was answerable at all, stripping the marker so the
    /// reader never sees the protocol.
    ///
    /// <para>Matching is tolerant for the same reason the summary delimiter's is — a model asked to
    /// reproduce a literal token sometimes decorates or rephrases it, and a strict match would leave
    /// "**NOT IN THE DATA**" sitting at the top of the answer while also reporting it as grounded.
    /// </para>
    /// </summary>
    public static (string Answer, bool Grounded) Split(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0)
        {
            return ("", true);
        }

        var lines = trimmed.Split('\n');
        if (!IsMarkerLine(lines[0]))
        {
            return (trimmed, true);
        }

        var rest = string.Join("\n", lines[1..]).Trim();
        return (rest.Length == 0
            // The marker alone is a refusal with no reason attached; give the reader the reason.
            ? "The measurements taken from this song do not cover that."
            : rest, false);
    }

    /// <summary>Long enough to be prose rather than a marker that has been decorated.</summary>
    private const int MaxMarkerLength = 60;

    private static bool IsMarkerLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxMarkerLength)
        {
            return false;
        }
        var letters = string.Concat(trimmed.Where(char.IsLetter)).ToUpperInvariant();
        return letters.StartsWith("NOTINTHEDATA", StringComparison.Ordinal);
    }

    /// <summary>
    /// Flattens one line of user or model text into the prompt. Newlines are collapsed so a pasted
    /// multi-line question cannot forge the "Q:"/"A:" structure the conversation block is built from.
    /// </summary>
    private static string Clean(string text)
    {
        var flattened = text.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= MaxQuestionLength ? flattened : flattened[..MaxQuestionLength];
    }
}
