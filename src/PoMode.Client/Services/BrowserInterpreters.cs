namespace PoMode.Client.Services;

/// <summary>
/// The two interpreters that run in the page rather than on the server: Chrome's built-in model (the
/// Prompt API) and WebLLM on WebGPU. Same contract for both (js/infer/browser-llm.js and web-llm.js
/// export the same two functions), so the panel picks a module by name and nothing else differs.
/// </summary>
public static class BrowserInterpreters
{
    public const string PromptApi = "BrowserSongInterpreter";
    public const string WebLlm = "WebLlmSongInterpreter";

    public static bool IsBrowser(string? name) => name is PromptApi or WebLlm;

    public static string ModuleFor(string name)
        => name == WebLlm ? "./js/infer/web-llm.js" : "./js/infer/browser-llm.js";
}
