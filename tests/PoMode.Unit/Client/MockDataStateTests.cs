using PoMode.Client.Services;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Client;

public sealed class MockDataStateTests
{
    [Fact]
    public void Default_state_is_live_so_cold_start_does_not_flash_the_banner()
    {
        // No data is on screen yet, mock or otherwise — flashing "USING MOCK DATA" on a fresh
        // client is misleading. SetMock() flips it on when a completed job's plan actually contains
        // a placeholder, which is the only honest trigger.
        Assert.False(new MockDataState().IsMockData);
    }

    [Fact]
    public void All_real_plan_is_not_mock()
    {
        var plan = new[]
        {
            new StagePlan(StageNames.Separating, ExecutionTier.Local, "OnnxStemSeparator"),
            new StagePlan(StageNames.PitchTracking, ExecutionTier.Local, "OnnxPitchTracker"),
            new StagePlan(StageNames.ChordDetecting, ExecutionTier.Local, "RealChordRecognizer"),
            new StagePlan(StageNames.ModalAnalysis, ExecutionTier.Local, "ModalAnalysisEngine"),
        };

        Assert.False(MockDataState.PlanContainsFakeExecutor(plan));
        // No plan at all cannot be vouched for, so it counts as mock — fail safe, not fail silent.
        Assert.True(MockDataState.PlanContainsFakeExecutor([]));
    }

    [Fact]
    public void Plan_with_a_placeholder_executor_is_mock()
    {
        var plan = new[]
        {
            new StagePlan(StageNames.Separating, ExecutionTier.Local, "OnnxStemSeparator"),
            new StagePlan(StageNames.PitchTracking, ExecutionTier.Local, "FakePitchTracker", IsPlaceholder: true),
            new StagePlan(StageNames.ChordDetecting, ExecutionTier.Local, "RealChordRecognizer"),
            new StagePlan(StageNames.ModalAnalysis, ExecutionTier.Local, "ModalAnalysisEngine"),
        };

        Assert.True(MockDataState.PlanContainsFakeExecutor(plan));
    }

}
