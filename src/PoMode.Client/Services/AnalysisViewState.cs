namespace PoMode.Client.Services;

/// <summary>
/// Controls whether the analysis workspace renders in Basic Mode (clean, single-screen player)
/// or Advanced Mode (full theory suite, modal HUD, stats dashboard, 3D landscape).
/// </summary>
public sealed class AnalysisViewState
{
    public string ViewMode { get; private set; } = "advanced";

    public bool IsBasic => ViewMode == "basic";
    public bool IsAdvanced => ViewMode == "advanced";

    public event Action? Changed;

    public void SetViewMode(string mode)
    {
        var normalized = mode.Equals("basic", StringComparison.OrdinalIgnoreCase) ? "basic" : "advanced";
        if (ViewMode != normalized)
        {
            ViewMode = normalized;
            Changed?.Invoke();
        }
    }
}
