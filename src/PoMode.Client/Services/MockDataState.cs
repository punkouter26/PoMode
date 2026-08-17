using PoMode.Shared.Analysis;

namespace PoMode.Client.Services;

/// <summary>True whenever displayed analysis data is mock/local. Real job results flip it via SetLive().</summary>
public sealed class MockDataState
{
    public bool IsMockData { get; private set; } = true;

    public event Action? Changed;

    public void SetLive()
    {
        if (IsMockData)
        {
            IsMockData = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Back to mock: a completed job whose plan still touched a fake executor.</summary>
    public void SetMock()
    {
        if (!IsMockData)
        {
            IsMockData = true;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// True when the plan is empty (unknown — treated conservatively as mock) or any stage ran on a
    /// <c>Fake*</c> executor (e.g. <c>FakePitchTracker</c>, <c>FakeChordRecognizer</c>). CLAUDE.md
    /// requires the "USING MOCK DATA" banner whenever displayed data is mock/local; a single fake
    /// stage makes the whole result partly fabricated, so any fake stage keeps the banner on.
    /// </summary>
    public static bool PlanContainsFakeExecutor(IReadOnlyList<StagePlan> plan)
        => plan.Count == 0 || plan.Any(p => p.Executor.StartsWith("Fake", StringComparison.Ordinal));
}
