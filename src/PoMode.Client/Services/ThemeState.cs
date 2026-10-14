using Microsoft.JSInterop;

namespace PoMode.Client.Services;

/// <summary>
/// The theme choice ("system", "light" or "dark"), shared by the header's quick toggle and the
/// Settings page so each shows what the other set. theme.js does the stamping and persisting.
/// </summary>
public sealed class ThemeState(IJSRuntime js)
{
    private IJSObjectReference? _module;

    public string Mode { get; private set; } = "system";

    public event Action? Changed;

    public async Task InitAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/shell/theme.js");
        Mode = await _module.InvokeAsync<string>("init");
        Changed?.Invoke();
    }

    public async Task CycleAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/shell/theme.js");
        Mode = await _module.InvokeAsync<string>("cycle");
        Changed?.Invoke();
    }

    public async Task SetAsync(string mode)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/shell/theme.js");
        Mode = await _module.InvokeAsync<string>("set", mode);
        Changed?.Invoke();
    }

    public string Label => Mode switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "Auto",
    };

    public string Glyph => Mode switch
    {
        "light" => "☀",
        "dark" => "☾",
        _ => "◐",
    };
}
