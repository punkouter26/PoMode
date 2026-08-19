namespace PoMode.Shared.Analysis;

/// <summary>
/// Display category of a selectable executor. Paid cloud executors and Fake placeholders are
/// never listed, so no other category exists on the wire. An enum (not a string) so the server
/// mapping and the client labels are both exhaustive and drift is a compile error.
/// </summary>
public enum ExecutorKind
{
    /// <summary>Runs a downloaded local model file (ONNX).</summary>
    Model,

    /// <summary>Plain code — no model, no network.</summary>
    Method,

    /// <summary>Client-delegated inference in the uploader's browser.</summary>
    Browser,
}

/// <summary>One selectable executor for a pipeline stage.</summary>
public sealed record ExecutorOptionDto(string Name, ExecutorKind Kind, bool Available, bool IsDefault);

/// <summary>All executors registered for one pipeline stage, in the planner's selection order.</summary>
public sealed record StageExecutorsDto(string Stage, IReadOnlyList<ExecutorOptionDto> Executors);
