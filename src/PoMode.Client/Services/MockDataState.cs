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
}
