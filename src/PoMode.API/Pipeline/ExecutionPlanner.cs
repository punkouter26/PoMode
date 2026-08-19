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
    /// Selection order across tiers, classic fallbacks *and* placeholders: real Local model (0) →
    /// real ClientDelegated (4) → classic model-less DSP (5) → any Fake* placeholder (6) → real
    /// Cloud (8). A classic-DSP alternative does real work, so it beats a placeholder — but it
    /// ranks after every model-backed free tier so registering one never changes a stage's default.
    /// A placeholder fabricates data, so it must lose to anything real and free — but it still
    /// beats Cloud, because falling through to mock data is free while falling through to a paid
    /// provider spends the user's money automatically.
    /// Used by both planning and mid-run fallback so the two can never disagree on order.
    /// </summary>
    public static int EffectiveRank(IStageExecutor executor) =>
        executor.IsPlaceholder ? 6
        : executor.IsClassicFallback ? 5
        : TierRank(executor.Tier) * 4;

    /// <summary>
    /// User-pickable executors: never paid Cloud (a picker click must not spend money) and never
    /// Fake placeholders (mock data stays a planner-ordered fallback). The one predicate both the
    /// options listing and the pick-honouring code use, so a crafted upload URL can select
    /// exactly what the pickers offer — nothing more.
    /// </summary>
    public static bool IsUserSelectable(IStageExecutor executor)
        => executor.Tier != ExecutionTier.Cloud && !executor.IsPlaceholder;

    /// <summary>"No picks": shared empty so the stage loop never null-checks.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoPicks =
        new Dictionary<string, string>();

    /// <summary>Plans with no browser help — the browser tier is invisible.</summary>
    public Task<List<StagePlan>> PlanAsync(CancellationToken ct)
        => PlanAsync(browserCanInfer: false, preferredExecutors: null, ct);

    /// <summary>
    /// Plans for one job. <paramref name="browserCanInfer"/> comes from what that job's browser said it
    /// could do, because Tier 2's availability is a property of the *client*, not of the server —
    /// <see cref="IStageExecutor.IsAvailableAsync"/> has no job to inspect, so the filtering happens here.
    /// <paramref name="preferredExecutors"/> (stage name → executor name) is the user's explicit
    /// per-stage pick from the upload form; an unknown or unavailable pick falls back to the normal
    /// ranked order rather than failing the job.
    /// </summary>
    public async Task<List<StagePlan>> PlanAsync(
        bool browserCanInfer, IReadOnlyDictionary<string, string>? preferredExecutors, CancellationToken ct)
    {
        preferredExecutors ??= NoPicks;
        return
        [
            await PlanStageAsync(StageNames.Separating, stemSeparators, browserCanInfer, preferredExecutors, ct),
            await PlanStageAsync(StageNames.PitchTracking, pitchTrackers, browserCanInfer, preferredExecutors, ct),
            await PlanStageAsync(StageNames.ChordDetecting, chordRecognizers, browserCanInfer, preferredExecutors, ct),
            new StagePlan(StageNames.ModalAnalysis, ExecutionTier.Local, "ModalAnalysisEngine"),
        ];
    }

    /// <summary>
    /// The selectable executors per stage for the home page's pickers. Lives on the planner so
    /// there is exactly one authority on stage structure, ordering, eligibility and defaults;
    /// availability is probed once per candidate and the browserless default falls out of the
    /// same probes. The modal stage is fixed code, listed for symmetry (same literal as
    /// <see cref="PlanAsync(bool, IReadOnlyDictionary{string, string}?, CancellationToken)"/>).
    /// </summary>
    public async Task<List<StageExecutorsDto>> ListOptionsAsync(CancellationToken ct) =>
    [
        await StageOptionsAsync(StageNames.Separating, stemSeparators, ct),
        await StageOptionsAsync(StageNames.PitchTracking, pitchTrackers, ct),
        await StageOptionsAsync(StageNames.ChordDetecting, chordRecognizers, ct),
        new StageExecutorsDto(StageNames.ModalAnalysis,
            [new ExecutorOptionDto("ModalAnalysisEngine", ExecutorKind.Method, Available: true, IsDefault: true)]),
    ];

    private static async Task<StageExecutorsDto> StageOptionsAsync<TExecutor>(
        string stage, IEnumerable<TExecutor> candidates, CancellationToken ct)
        where TExecutor : IStageExecutor
    {
        // One availability probe per candidate; the default is what a browserless, pick-less
        // PlanAsync would run: the first available candidate by rank outside the browser tier.
        var probed = new List<(IStageExecutor Executor, bool Available)>();
        foreach (var candidate in candidates.OrderBy(c => EffectiveRank(c)))
        {
            probed.Add((candidate, await candidate.IsAvailableAsync(ct)));
        }
        var planned = probed
            .FirstOrDefault(p => p.Available && p.Executor.Tier != ExecutionTier.ClientDelegated)
            .Executor;
        return new StageExecutorsDto(stage,
        [
            .. probed
                .Where(p => IsUserSelectable(p.Executor))
                .Select(p => new ExecutorOptionDto(
                    p.Executor.Name, KindOf(p.Executor), p.Available, IsDefault: p.Executor == planned)),
        ]);
    }

    /// <summary>Only user-selectable executors reach this, so three kinds cover everything.</summary>
    private static ExecutorKind KindOf(IStageExecutor executor) => executor switch
    {
        { Tier: ExecutionTier.ClientDelegated } => ExecutorKind.Browser,
        { UsesLocalModel: true } => ExecutorKind.Model,
        _ => ExecutorKind.Method,
    };

    private static async Task<StagePlan> PlanStageAsync<TExecutor>(
        string stage,
        IEnumerable<TExecutor> candidates,
        bool browserCanInfer,
        IReadOnlyDictionary<string, string> preferredExecutors,
        CancellationToken ct)
        where TExecutor : IStageExecutor
    {
        var eligible = candidates
            .Where(c => browserCanInfer || c.Tier != ExecutionTier.ClientDelegated)
            .ToArray();

        if (preferredExecutors.TryGetValue(stage, out var preferredName))
        {
            foreach (var candidate in eligible)
            {
                if (candidate.Name == preferredName
                    && IsUserSelectable(candidate)
                    && await candidate.IsAvailableAsync(ct))
                {
                    return new StagePlan(stage, candidate.Tier, candidate.Name, candidate.IsPlaceholder);
                }
            }
            // The pick is unknown, not user-selectable, or unavailable — the ranked order below
            // decides instead.
        }

        foreach (var candidate in eligible.OrderBy(c => EffectiveRank(c)))
        {
            if (await candidate.IsAvailableAsync(ct))
            {
                return new StagePlan(stage, candidate.Tier, candidate.Name, candidate.IsPlaceholder);
            }
        }
        throw new InvalidOperationException($"No executor is available for stage {stage}.");
    }
}
