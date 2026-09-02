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
    public async Task Planning_PrefersLocalAndSkipsUnavailableExecutors()
    {
        var planner = Planner(
            new StubExecutor("CloudX", ExecutionTier.Cloud, available: true),
            new StubExecutor("BrowserX", ExecutionTier.ClientDelegated, available: true),
            new StubExecutor("LocalX", ExecutionTier.Local, available: true));

        var plan = await planner.PlanAsync(CancellationToken.None);
        Assert.Equal("LocalX", plan[0].Executor);
        Assert.Equal(ExecutionTier.Local, plan[0].Tier);

        var fallbackPlanner = Planner(
            new StubExecutor("LocalX", ExecutionTier.Local, available: false),
            new StubExecutor("CloudX", ExecutionTier.Cloud, available: true));
        var fallbackPlan = await fallbackPlanner.PlanAsync(CancellationToken.None);
        Assert.Equal("CloudX", fallbackPlan[0].Executor);
    }

    [Fact]
    public async Task Produces_all_four_stages_in_order_with_fixed_modal_stage()
    {
        var planner = Planner(new StubExecutor("LocalX", ExecutionTier.Local, available: true));
        var plan = await planner.PlanAsync(CancellationToken.None);

        Assert.Equal(4, plan.Count);
        Assert.Equal(StageNames.Separating, plan[0].Stage);
        Assert.Equal(StageNames.PitchTracking, plan[1].Stage);
        Assert.Equal(StageNames.ChordDetecting, plan[2].Stage);
        Assert.Equal(StageNames.ModalAnalysis, plan[3].Stage);
        Assert.Equal("ModalAnalysisEngine", plan[3].Executor);
    }

    [Fact]
    public async Task ListOptions_OmitsPlaceholdersAndRanksCandidates()
    {
        var planner = Planner(
            new StubExecutor("RealLocal", ExecutionTier.Local, available: true),
            new StubExecutor("Placeholder", ExecutionTier.Local, available: true, placeholder: true));

        var options = await planner.ListOptionsAsync(CancellationToken.None);
        Assert.NotEmpty(options);
        var sep = Assert.Single(options, s => s.Stage == StageNames.Separating);
        Assert.Contains(sep.Executors, o => o.Name == "RealLocal");
        Assert.DoesNotContain(sep.Executors, o => o.Name == "Placeholder");
    }

    [Fact]
    public async Task UserPreferences_AreHonoredWhenAvailable()
    {
        var planner = Planner(
            new StubExecutor("Default", ExecutionTier.Local, available: true),
            new StubExecutor("UserPick", ExecutionTier.Local, available: true));

        var picks = new Dictionary<string, string> { [StageNames.Separating] = "UserPick" };
        var plan = await planner.PlanAsync(browserCanInfer: false, picks, CancellationToken.None);
        Assert.Equal("UserPick", plan[0].Executor);
    }
}
