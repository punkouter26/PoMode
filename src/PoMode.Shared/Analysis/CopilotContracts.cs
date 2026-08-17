namespace PoMode.Shared.Analysis;

/// <summary>Asks the copilot to explain one analysis window.</summary>
public sealed record CopilotRequest(string JobId, int WindowIndex);

/// <summary>
/// The copilot's answer. It is always optional (spec §5): with no Ollama running,
/// <paramref name="Available"/> is false and <paramref name="Reason"/> says why, so the UI can show a
/// plain "unavailable" card. This shape never carries an error status — a missing copilot is a normal
/// state, not a failure.
/// </summary>
public sealed record CopilotReply(bool Available, string? Markdown, string? Model, string? Reason);
