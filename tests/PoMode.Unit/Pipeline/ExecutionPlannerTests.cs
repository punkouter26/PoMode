using Xunit;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.Unit.Pipeline;

public class ExecutionPlannerTests
{
    private sealed class StubExecutor(string name, ExecutionTier tier, bool available)
        : IStemSeparator, IPitchTracker, IChordRecognizer
    {
        public string Name => name;
        public ExecutionTier Tier => tier;
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
    public void Tier_rank_orders_local_first_cloud_last()
    {
        Assert.True(ExecutionPlanner.TierRank(ExecutionTier.Local)
            < ExecutionPlanner.TierRank(ExecutionTier.ClientDelegated));
        Assert.True(ExecutionPlanner.TierRank(ExecutionTier.ClientDelegated)
            < ExecutionPlanner.TierRank(ExecutionTier.Cloud));
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
        var withBrowser = await planner.PlanAsync(browserCanInfer: true, CancellationToken.None);

        Assert.Equal("CloudX", withoutBrowser[1].Executor);
        Assert.Equal("BrowserX", withBrowser[1].Executor);
    }

    [Fact]
    public async Task A_capable_browser_still_loses_to_a_working_local_executor()
    {
        var planner = Planner(
            new StubExecutor("BrowserX", ExecutionTier.ClientDelegated, available: true),
            new StubExecutor("LocalX", ExecutionTier.Local, available: true));

        var plan = await planner.PlanAsync(browserCanInfer: true, CancellationToken.None);

        Assert.Equal("LocalX", plan[1].Executor);
        Assert.Equal(ExecutionTier.Local, plan[1].Tier);
    }
}
