using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.JSInterop;
using PoMode.Shared.Analysis;

namespace PoMode.Client.Services;

/// <summary>
/// What this browser remembers between visits, all optional: one typed read and write over
/// localStorage. Every page used to hand-roll its own string format, parser and try/catch (tabs for
/// the library filters, <c>;</c> and <c>=</c> for the model pins, <c>|</c> for Practice), and each
/// swallowed a different set of exceptions. Blocked site data, a private window or a value written
/// by an older build all read as "nothing remembered".
/// </summary>
public sealed class BrowserPrefs(IJSRuntime js)
{
    public const string LibraryFiltersKey = "pm-library-filters";
    public const string ExecutorPinsKey = "pm-executor-pins";
    public const string PracticeBackingKey = "pm-practice-backing";
    public const string InterpreterKey = "pm-interpreter";

    public async Task<T?> GetAsync<T>(string key, JsonTypeInfo<T> type)
    {
        try
        {
            return await js.InvokeAsync<string?>("localStorage.getItem", key) is { } raw
                ? JsonSerializer.Deserialize(raw, type)
                : default;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or JsonException or InvalidOperationException)
        {
            return default;
        }
    }

    /// <summary>Fire-and-forget safe: a browser that refuses storage just forgets.</summary>
    public async Task SetAsync<T>(string key, T value, JsonTypeInfo<T> type)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", key, JsonSerializer.Serialize(value, type));
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
        }
    }

    public async Task RemoveAsync(string key)
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.removeItem", key);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
        }
    }
}

/// <summary>The Library's search and filters as they were left.</summary>
public sealed record LibraryFilters(string Search);

/// <summary>Practice's backing as it was left, and whether the click was on. A value saved before the
/// click existed reads as on.</summary>
public sealed record PracticeBacking(int TonicPitchClass, ScaleMode Mode, string ProgressionId, double Bpm, bool Click = true);

[JsonSerializable(typeof(LibraryFilters))]
[JsonSerializable(typeof(PracticeBacking))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
internal sealed partial class PrefsJsonContext : JsonSerializerContext;
