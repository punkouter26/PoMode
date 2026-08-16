using PoMode.Shared.Analysis;

namespace PoMode.API.Pipeline;

/// <summary>Resolves each stage to the best available executor: Local → ClientDelegated → Cloud (paid last).</summary>
public sealed class ExecutionPlanner(
    IEnumerable<IStemSeparator> stemSeparators,
    IEnumerable<IPitchTracker> pitchTrackers,
    IEnumerable<IChordRecognizer> chordRecognizers)
{
    public static int TierRank(ExecutionTier tier) => tier switch
    {
        ExecutionTier.Local => 0,
        ExecutionTier.ClientDelegated => 1,
        ExecutionTier.Cloud => 2,
        _ => int.MaxValue,
    };

    public async Task<List<StagePlan>> PlanAsync(CancellationToken ct) =>
    [
        await PlanStageAsync(StageNames.Separating, stemSeparators, ct),
        await PlanStageAsync(StageNames.PitchTracking, pitchTrackers, ct),
        await PlanStageAsync(StageNames.ChordDetecting, chordRecognizers, ct),
        new StagePlan(StageNames.ModalAnalysis, ExecutionTier.Local, "ModalAnalysisEngine"),
    ];

    private static async Task<StagePlan> PlanStageAsync<TExecutor>(
        string stage, IEnumerable<TExecutor> candidates, CancellationToken ct)
        where TExecutor : IStageExecutor
    {
        foreach (var candidate in candidates.OrderBy(c => TierRank(c.Tier)))
        {
            if (await candidate.IsAvailableAsync(ct))
            {
                return new StagePlan(stage, candidate.Tier, candidate.Name);
            }
        }
        throw new InvalidOperationException($"No executor is available for stage {stage}.");
    }
}
