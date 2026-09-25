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

/// <summary>
/// What a person reads for an executor name the server records. The recorded names are class names
/// (plus the markers a seeded job writes for a stage it provided), which is right for
/// <c>StageHistory</c> and <c>/diag</c> and wrong on a page: "OnnxPitchTracker" says nothing to someone
/// who wanted to know what heard their melody. An unknown name falls through as itself, so a new
/// executor shows up under its class name until it is added here.
/// </summary>
public static class ExecutorNames
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["OnnxStemSeparator"] = "HTDemucs",
        ["FakeStemSeparator"] = "Placeholder separation",
        ["SkippedDryVocal"] = "Skipped (solo voice)",
        ["RmvpePitchTracker"] = "RMVPE",
        ["OnnxPitchTracker"] = "Basic Pitch",
        ["ClientDelegatedPitchTracker"] = "Basic Pitch (your browser)",
        ["YinPitchTracker"] = "YIN",
        ["FakePitchTracker"] = "Placeholder melody",
        ["ChromaChordRecognizer"] = "Chroma matching",
        ["ViterbiChordRecognizer"] = "Viterbi decoding",
        ["FakeChordRecognizer"] = "Placeholder chords",
        ["ModeLabBacking"] = "The backing you sang over",
        ["DemoScore"] = "Written score",
        ["BeatThisBeatTracker"] = "Beat This!",
        ["DspBeatTracker"] = "Tempo estimator",
        ["ModalAnalysisEngine"] = "Modal analysis",
        ["OllamaSongInterpreter"] = "Ollama",
        ["TemplateSongInterpreter"] = "Built-in writer",
    };

    public static string Display(string name) => Names.GetValueOrDefault(name, name);
}
