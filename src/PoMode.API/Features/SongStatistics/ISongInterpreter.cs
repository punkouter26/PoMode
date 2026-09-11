using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.SongStatistics;

/// <summary>
/// Turns the computed <see cref="SongStats"/> into prose a musician can read.
///
/// <para>It extends <see cref="IStageExecutor"/> even though interpretation is not a pipeline stage,
/// so the tier vocabulary, the availability probe and — importantly —
/// <see cref="ExecutionPlanner.EffectiveRank"/> and <see cref="ExecutionPlanner.IsUserSelectable"/>
/// apply unchanged. That ordering is exactly what is wanted here too: a free local model first, then
/// the deterministic template, and a paid cloud model only when the user asks for it by name.</para>
///
/// <para>Every implementation is handed the same prompt from <see cref="InterpretationPrompt"/> and
/// is forbidden to add facts. The statistics are computed; only the wording is generated.</para>
/// </summary>
public interface ISongInterpreter : IStageExecutor
{
    Task<string> InterpretAsync(SongStats stats, CancellationToken ct);

    /// <summary>
    /// Answers one follow-up question about the same measurements.
    ///
    /// <para>On the same seam rather than a separate one because it is the same capability asked a
    /// narrower question, and splitting it would mean a second availability probe and a second
    /// ranking for the same Ollama socket. The grounding rule is unchanged and matters more here:
    /// an answer given in response to a direct question carries more authority with a reader than a
    /// summary does, so an implementation that cannot answer from the data must say so rather than
    /// fill the gap.</para>
    ///
    /// <para>Returns the raw answer; <c>QuestionPrompt.Split</c> separates the refusal marker from
    /// the prose, so every implementation signals "not in the data" the same way and the selector
    /// needs no per-implementation handling.</para>
    /// </summary>
    Task<string> AnswerAsync(
        SongStats stats, IReadOnlyList<InterpretationTurn>? history, string question, CancellationToken ct);
}
