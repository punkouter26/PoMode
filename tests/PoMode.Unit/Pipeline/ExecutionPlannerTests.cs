using Xunit;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.Unit.Pipeline;

public class ExecutionPlannerTests
{
    private sealed class StubExecutor(
        string name, ExecutionTier tier, bool available, bool placeholder = false, bool classicFallback = false)
        : IStemSeparator, IPitchTracker, IChordRecognizer
    {
        public string Name => name;
        public ExecutionTier Tier => tier;
        public bool IsPlaceholder => placeholder;
        public bool IsClassicFallback => classicFallback;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(available);
        public Task SeparateAsync(StageContext context, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<NoteEvent>>([]);
        public Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ChordSpan>>([]);
    }

    private static ExecutionPlanner Planner(params StubExecutor[] executors)
        => new(executors, executors, executors);

    [Fact]
    public async Task Prefers_local_over_client_delegated_over_cloud()
    {
        var planner = Planner(
            new StubExecutor("CloudX", ExecutionTier.Cloud, available: true),
            new StubExecutor("BrowserX", ExecutionTier.ClientDelegated, available: true),
            new StubExecutor("LocalX", ExecutionTier.Local, available: true));

        var plan = await planner.PlanAsync(CancellationToken.None);

        Assert.Equal("LocalX", plan[0].Executor);
        Assert.Equal(ExecutionTier.Local, plan[0].Tier);
    }

    [Fact]
    public async Task Skips_unavailable_executors()
    {
        var planner = Planner(
            new StubExecutor("LocalX", ExecutionTier.Local, available: false),
            new StubExecutor("CloudX", ExecutionTier.Cloud, available: true));

        var plan = await planner.PlanAsync(CancellationToken.None);

        Assert.Equal("CloudX", plan[0].Executor);
    }

    [Fact]
    public async Task Produces_all_four_stages_in_order_with_fixed_modal_stage()
    {
        var planner = Planner(new StubExecutor("LocalX", ExecutionTier.Local, available: true));

        var plan = await planner.PlanAsync(CancellationToken.None);

        Assert.Equal(
            [StageNames.Separating, StageNames.PitchTracking, StageNames.ChordDetecting, StageNames.ModalAnalysis],
            plan.Select(p => p.Stage).ToArray());
        Assert.Equal("ModalAnalysisEngine", plan[3].Executor);
        Assert.Equal(ExecutionTier.Local, plan[3].Tier);
    }

    [Fact]
    public async Task Throws_naming_the_stage_when_nothing_is_available()
    {
        var planner = Planner(new StubExecutor("LocalX", ExecutionTier.Local, available: false));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => planner.PlanAsync(CancellationToken.None));

        Assert.Contains(StageNames.Separating, exception.Message);
    }

    [Fact]
    public async Task The_browser_tier_is_invisible_unless_that_jobs_browser_can_run_inference()
    {
        // Tier 2 availability is a property of the client, not the server, so a registered browser
        // executor must not be planned for a job whose browser never said it could help.
        var planner = Planner(
            new StubExecutor("CloudX", ExecutionTier.Cloud, available: true),
            new StubExecutor("BrowserX", ExecutionTier.ClientDelegated, available: true));

        var withoutBrowser = await planner.PlanAsync(CancellationToken.None);
        var withBrowser = await planner.PlanAsync(browserCanInfer: true, null, CancellationToken.None);

        Assert.Equal("CloudX", withoutBrowser[1].Executor);
        Assert.Equal("BrowserX", withBrowser[1].Executor);
    }

    /// <summary>
    /// The whole selection contract in one place: local model → browser → classic model-less DSP →
    /// Fake placeholder → paid cloud. Every rule this pins was previously a separate scenario test;
    /// asserting the rank function directly is stricter, because it also forbids ties.
    /// Money safety lives in the last two links — mock data must beat cloud so an automatic
    /// fallback can never spend the user's money, and a real free executor must beat mock data.
    /// </summary>
    [Fact]
    public void The_effective_rank_order_is_local_browser_classic_placeholder_then_cloud()
    {
        var local = new StubExecutor("LocalX", ExecutionTier.Local, available: true);
        var browser = new StubExecutor("BrowserX", ExecutionTier.ClientDelegated, available: true);
        var classic = new StubExecutor("ClassicX", ExecutionTier.Local, available: true, classicFallback: true);
        var placeholder = new StubExecutor("FakeX", ExecutionTier.Local, available: true, placeholder: true);
        var cloud = new StubExecutor("CloudX", ExecutionTier.Cloud, available: true);

        var ranks = new[] { local, browser, classic, placeholder, cloud }
            .Select(ExecutionPlanner.EffectiveRank)
            .ToArray();

        for (var i = 1; i < ranks.Length; i++)
        {
            Assert.True(ranks[i - 1] < ranks[i], $"rank {i - 1} ({ranks[i - 1]}) must beat rank {i} ({ranks[i]})");
        }
    }

    [Fact]
    public async Task Planning_follows_that_rank_order_when_every_kind_is_registered()
    {
        // The end-to-end counterpart of the rank test: with all five kinds available and a capable
        // browser, the local model still wins — registering alternatives never moves the default.
        var planner = Planner(
            new StubExecutor("CloudX", ExecutionTier.Cloud, available: true),
            new StubExecutor("FakeX", ExecutionTier.Local, available: true, placeholder: true),
            new StubExecutor("ClassicX", ExecutionTier.Local, available: true, classicFallback: true),
            new StubExecutor("BrowserX", ExecutionTier.ClientDelegated, available: true),
            new StubExecutor("LocalX", ExecutionTier.Local, available: true));

        var plan = await planner.PlanAsync(browserCanInfer: true, null, CancellationToken.None);

        Assert.Equal("LocalX", plan[0].Executor);
        Assert.Equal(ExecutionTier.Local, plan[0].Tier);
    }

    [Fact]
    public async Task An_explicit_pick_overrides_the_ranked_order_for_that_stage_only()
    {
        var planner = Planner(
            new StubExecutor("LocalX", ExecutionTier.Local, available: true),
            new StubExecutor("ClassicX", ExecutionTier.Local, available: true, classicFallback: true));

        var plan = await planner.PlanAsync(
            browserCanInfer: false,
            new Dictionary<string, string> { [StageNames.PitchTracking] = "ClassicX" },
            CancellationToken.None);

        Assert.Equal("ClassicX", plan[1].Executor); // the picked stage honours the pick
        Assert.Equal("LocalX", plan[0].Executor);   // unpicked stages keep the ranked default
    }

    [Fact]
    public async Task A_cloud_executor_cannot_be_picked_by_name()
    {
        // A crafted upload query param must never be able to spend money directly.
        var planner = Planner(
            new StubExecutor("CloudX", ExecutionTier.Cloud, available: true),
            new StubExecutor("LocalX", ExecutionTier.Local, available: true));

        var plan = await planner.PlanAsync(
            browserCanInfer: false,
            new Dictionary<string, string> { [StageNames.Separating] = "CloudX" },
            CancellationToken.None);

        Assert.Equal("LocalX", plan[0].Executor);
    }

    [Fact]
    public async Task An_unknown_or_unavailable_pick_falls_back_to_the_ranked_order()
    {
        var planner = Planner(
            new StubExecutor("LocalX", ExecutionTier.Local, available: true),
            new StubExecutor("BrokenX", ExecutionTier.Local, available: false));

        var plan = await planner.PlanAsync(
            browserCanInfer: false,
            new Dictionary<string, string>
            {
                [StageNames.PitchTracking] = "BrokenX",
                [StageNames.ChordDetecting] = "NoSuchExecutor",
            },
            CancellationToken.None);

        Assert.Equal("LocalX", plan[1].Executor);
        Assert.Equal("LocalX", plan[2].Executor);
    }

}
