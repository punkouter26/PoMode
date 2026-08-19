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
}
