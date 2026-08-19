namespace PoMode.Client.Services;

/// <summary>
/// Controls whether the analysis workspace renders in Basic Mode (clean, single-screen player)
/// or Advanced Mode (full theory suite, modal HUD, stats dashboard, 3D landscape). One stored
/// bit; the string form exists only at the boundary (nav clicks, the ?view= query).
/// </summary>
public sealed class AnalysisViewState
{
    public bool IsBasic { get; private set; }

    public bool IsAdvanced => !IsBasic;

    public event Action? Changed;

    public void SetViewMode(string mode)
    {
        var basic = mode.Equals("basic", StringComparison.OrdinalIgnoreCase);
        if (IsBasic != basic)
        {
            IsBasic = basic;
            Changed?.Invoke();
        }
    }
}
