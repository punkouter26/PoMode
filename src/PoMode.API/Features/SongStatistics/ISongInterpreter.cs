using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Turns the computed <see cref="SongStats"/> into prose a musician can read.
///
/// <para>It extends <see cref="IStageExecutor"/> even though interpretation is not a pipeline stage,
/// so the tier vocabulary and the availability probe apply unchanged.</para>
///
/// <para>Every implementation answers the same <see cref="ChatPrompt"/>, from
/// <see cref="InterpretationPrompt"/> or <see cref="QuestionPrompt"/>, and is forbidden to add facts.
/// The statistics are computed; only the wording is generated.</para>
/// </summary>
public interface ISongInterpreter : IStageExecutor
{
    /// <summary>
    /// Writes the reply to <see cref="InterpretationRequest.Prompt"/> as JSON fitting its schema,
    /// streamed in pieces as it is written, so the reader sees the first words in under a second
    /// rather than after the whole paragraph.
    ///
    /// <para>One method for the summary and the follow-up question: they are the same capability
    /// asked a narrower question, and the prompt says which.</para>
    /// </summary>
    IAsyncEnumerable<string> ReplyAsync(InterpretationRequest request, CancellationToken ct);
}

/// <summary>
/// One thing to write. <see cref="Prompt"/> is what a model is sent; <see cref="Stats"/> and
/// <see cref="Question"/> (null for the summary) are what the template writer reads instead.
/// </summary>
public sealed record InterpretationRequest(SongStats Stats, string? Question, ChatPrompt Prompt);
