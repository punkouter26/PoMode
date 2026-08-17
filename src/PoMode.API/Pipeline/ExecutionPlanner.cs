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

    /// <summary>
    /// Selection order across tiers *and* placeholders: real Local (0) → real ClientDelegated (2) →
    /// any Fake* placeholder (3) → real Cloud (4). A placeholder fabricates data, so it must lose to
    /// a browser doing real inference — but it still beats Cloud, because falling through to mock
    /// data is free while falling through to a paid provider spends the user's money automatically.
    /// Used by both planning and mid-run fallback so the two can never disagree on order.
    /// </summary>
    public static int EffectiveRank(IStageExecutor executor) =>
        executor.IsPlaceholder ? 3 : TierRank(executor.Tier) * 2;

    /// <summary>Plans with no browser help — the browser tier is invisible.</summary>
    public Task<List<StagePlan>> PlanAsync(CancellationToken ct)
        => PlanAsync(browserCanInfer: false, ct);

    /// <summary>
    /// Plans for one job. <paramref name="browserCanInfer"/> comes from what that job's browser said it
    /// could do, because Tier 2's availability is a property of the *client*, not of the server —
    /// <see cref="IStageExecutor.IsAvailableAsync"/> has no job to inspect, so the filtering happens here.
    /// </summary>
    public async Task<List<StagePlan>> PlanAsync(bool browserCanInfer, CancellationToken ct) =>
    [
        await PlanStageAsync(StageNames.Separating, stemSeparators, browserCanInfer, ct),
        await PlanStageAsync(StageNames.PitchTracking, pitchTrackers, browserCanInfer, ct),
        await PlanStageAsync(StageNames.ChordDetecting, chordRecognizers, browserCanInfer, ct),
        new StagePlan(StageNames.ModalAnalysis, ExecutionTier.Local, "ModalAnalysisEngine"),
    ];

    private static async Task<StagePlan> PlanStageAsync<TExecutor>(
        string stage, IEnumerable<TExecutor> candidates, bool browserCanInfer, CancellationToken ct)
        where TExecutor : IStageExecutor
    {
        var eligible = candidates
            .Where(c => browserCanInfer || c.Tier != ExecutionTier.ClientDelegated);

        foreach (var candidate in eligible.OrderBy(c => EffectiveRank(c)))
        {
            if (await candidate.IsAvailableAsync(ct))
            {
                return new StagePlan(stage, candidate.Tier, candidate.Name);
            }
        }
        throw new InvalidOperationException($"No executor is available for stage {stage}.");
    }
}
