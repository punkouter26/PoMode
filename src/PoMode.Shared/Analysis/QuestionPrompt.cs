using System.Text;

namespace PoMode.Shared.Analysis;

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
/// answer in a list of intervals — and a model with no way to say so will invent one. So the reply
/// carries <see cref="InDataField"/>, and the client labels an answer that sets it false rather than
/// hiding the refusal.</para>
/// </summary>
public static class QuestionPrompt
{
    public const string InDataField = "inData";
    public const string AnswerField = "answer";

    /// <summary>
    /// The reply's shape. <c>inData</c> comes first so a model commits to whether the data covers the
    /// question before it starts answering, rather than deciding halfway through a paragraph. It
    /// replaced a "NOT IN THE DATA" first line, which models decorated ("**NOT IN THE DATA**") often
    /// enough to need a tolerant matcher of its own.
    /// </summary>
    public const string Schema =
        """{"type":"object","properties":{"inData":{"type":"boolean"},"answer":{"type":"string"}},"required":["inData","answer"]}""";

    /// <summary>
    /// Exchanges carried into the prompt, newest kept. A local model's context is finite and the
    /// measurements are worth more than the eighth exchange, so the tail is what gets dropped from
    /// the front.
    /// </summary>
    public const int MaxHistoryTurns = 6;

    /// <summary>Longer than this and the "question" is a document. Truncated rather than refused,
    /// because a user who pastes a paragraph still deserves an answer to the first part of it.</summary>
    public const int MaxQuestionLength = 500;

    /// <summary>
    /// What a question asks for, after the statistics and before the conversation. The rules every
    /// request shares are <see cref="InterpretationPrompt.System"/>, sent as the system message here
    /// too, so a question after the summary reuses its processed prefix.
    /// </summary>
    public const string Task =
        "Answer one question about this song.\n"
        + "- You may explain, compare and reason about the figures, but not add new ones.\n"
        + "- Reply with a JSON object: \"inData\" is true when the measurements answer the question, and "
        + "\"answer\" is your answer.\n"
        + "- If the measurements do not contain what was asked, set inData to false and make the answer "
        + "one sentence saying what would be needed to answer it. Do this rather than guessing.\n"
        + "- Match the questioner's level. If they use theory terms, use them back; if they do not, "
        + "explain in ordinary words.\n"
        + "- Be direct. Lead with the answer, then the evidence for it.\n"
        + "- One or two short paragraphs.";

    /// <summary>The whole request for one question.</summary>
    public static ChatPrompt For(SongStats stats, IReadOnlyList<InterpretationTurn>? history, string question)
        => new(InterpretationPrompt.System, [new ChatMessage("user", User(stats, history, question))], Schema);

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
        text.AppendLine(Task);

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
    /// Flattens one line of user or model text into the prompt. Newlines are collapsed so a pasted
    /// multi-line question cannot forge the "Q:"/"A:" structure the conversation block is built from.
    /// </summary>
    private static string Clean(string text)
    {
        var flattened = text.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= MaxQuestionLength ? flattened : flattened[..MaxQuestionLength];
    }
}
