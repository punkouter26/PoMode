using PoMode.Shared.Analysis;

namespace PoMode.Client.Services;

/// <summary>
/// True whenever displayed analysis data is mock/local. Real job results flip it via SetLive().
/// Defaults to <c>false</c>: a fresh client has nothing to show yet, so flashing "USING MOCK DATA"
/// on cold-start was misleading — no data was on screen, mock or otherwise. A completed job whose
/// plan actually touched a placeholder/Fake executor still flips it back on via SetMock().
/// </summary>
public sealed class MockDataState
{
    public bool IsMockData { get; private set; } = false;

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
    /// placeholder executor (the server marks those via <see cref="StagePlan.IsPlaceholder"/>).
    /// The <c>Fake*</c> name check stays as a fallback: plans persisted before the flag existed
    /// deserialize with <c>IsPlaceholder = false</c>, and jobs live for days — without it those
    /// jobs would render fabricated data with no banner. CLAUDE.md requires the "USING MOCK DATA"
    /// banner whenever displayed data is mock/local; a single fake stage makes the whole result
    /// partly fabricated, so any fake stage keeps the banner on.
    /// </summary>
    public static bool PlanContainsFakeExecutor(IReadOnlyList<StagePlan> plan)
        => plan.Count == 0
            || plan.Any(p => p.IsPlaceholder || p.Executor.StartsWith("Fake", StringComparison.Ordinal));
}
