namespace PoMode.Client.Services;

/// <summary>True whenever displayed analysis data is mock/local. Real job results set this false (Phase 2+).</summary>
public sealed class MockDataState
{
    public bool IsMockData { get; set; } = true;
}
