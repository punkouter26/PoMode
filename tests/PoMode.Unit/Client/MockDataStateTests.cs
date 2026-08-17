using PoMode.API.Pipeline;
using PoMode.Client.Services;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Client;

public sealed class MockDataStateTests
{
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
    }

    [Fact]
    public void Plan_with_a_fake_executor_is_mock()
    {
        var plan = new[]
        {
            new StagePlan(StageNames.Separating, ExecutionTier.Local, "OnnxStemSeparator"),
            new StagePlan(StageNames.PitchTracking, ExecutionTier.Local, "FakePitchTracker"),
            new StagePlan(StageNames.ChordDetecting, ExecutionTier.Local, "RealChordRecognizer"),
            new StagePlan(StageNames.ModalAnalysis, ExecutionTier.Local, "ModalAnalysisEngine"),
        };

        Assert.True(MockDataState.PlanContainsFakeExecutor(plan));
    }

    [Fact]
    public void Empty_plan_is_mock()
    {
        Assert.True(MockDataState.PlanContainsFakeExecutor([]));
    }

    [Fact]
    public void SetMock_flips_from_live_and_raises_changed()
    {
        var state = new MockDataState();
        state.SetLive();
        Assert.False(state.IsMockData);

        var raised = false;
        state.Changed += () => raised = true;
        state.SetMock();

        Assert.True(state.IsMockData);
        Assert.True(raised);
    }
}
