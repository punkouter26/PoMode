using PoMode.Shared.Analysis;

namespace PoMode.Client.Services;

/// <summary>One entry per stage chooser; "" = Auto (the server's ranked default).</summary>
public sealed record ExecutorChoice(string Value, string Label);

/// <summary>
/// The analysis-model pins: which executor the user wants for a stage when the planner would
/// otherwise pick by rank. Chosen on the Settings page, applied by every upload from the home page.
/// </summary>
public static class ExecutorChoices
{
    public static string StageLabel(string stage) => stage switch
    {
        StageNames.Separating => "Separation",
        StageNames.PitchTracking => "Melody",
        StageNames.ChordDetecting => "Chords",
        StageNames.ModalAnalysis => "Modes",
        _ => stage,
    };

    private static string KindLabel(ExecutorKind kind) => kind switch
    {
        ExecutorKind.Model => "local model",
        ExecutorKind.Method => "code",
        ExecutorKind.Browser => "browser model",
        _ => kind.ToString(),
    };

    /// <summary>
    /// "Auto" stays unnamed on purpose: what the server actually picks can depend on the uploading
    /// browser's capability, so naming one executor here could advertise the wrong one. A browser-tier
    /// option is offered only to a browser whose Tier-2 probe succeeded; the planner ignores it otherwise.
    /// </summary>
    public static List<ExecutorChoice> For(StageExecutorsDto stage, bool browserCapable)
    {
        var choices = new List<ExecutorChoice>(stage.Executors.Count + 1) { new("", "Auto") };
        foreach (var option in stage.Executors)
        {
            if (option.Kind == ExecutorKind.Browser && !browserCapable)
            {
                continue;
            }
            var suffix = option.Available ? "" : " — unavailable";
            choices.Add(new ExecutorChoice(option.Name, $"{ExecutorNames.Rated(option.Name)} ({KindLabel(option.Kind)}){suffix}"));
        }
        return choices;
    }

    /// <summary>The saved pins that still name an executor the server lists; the planner would ignore
    /// a stale one anyway, but nothing should count or show it.</summary>
    public static async Task<Dictionary<string, string>> LoadAsync(BrowserPrefs prefs, IReadOnlyList<StageExecutorsDto>? stages)
    {
        var saved = await prefs.GetAsync(BrowserPrefs.ExecutorPinsKey, PrefsJsonContext.Default.DictionaryStringString) ?? [];
        return saved
            .Where(pin => stages?.Any(s => s.Stage == pin.Key && s.Executors.Any(e => e.Name == pin.Value)) == true)
            .ToDictionary(pin => pin.Key, pin => pin.Value);
    }

    public static Task SaveAsync(BrowserPrefs prefs, Dictionary<string, string> pins)
        => prefs.SetAsync(BrowserPrefs.ExecutorPinsKey, pins, PrefsJsonContext.Default.DictionaryStringString);
}
