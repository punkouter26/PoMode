using PoMode.Shared.Analysis;

namespace PoMode.API.Pipeline;

public sealed record StageContext(string JobId, string JobDir, string InputPath);

public static class StageNames
{
    public const string Separating = "Separating";
    public const string PitchTracking = "PitchTracking";
    public const string ChordDetecting = "ChordDetecting";
    public const string ModalAnalysis = "ModalAnalysis";
}

public interface IStageExecutor
{
    string Name { get; }
    ExecutionTier Tier { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct);
}

public interface IStemSeparator : IStageExecutor
{
    /// <summary>Writes vocals.wav and instrumental.wav into <see cref="StageContext.JobDir"/>.</summary>
    Task SeparateAsync(StageContext context, CancellationToken ct);
}

public interface IPitchTracker : IStageExecutor
{
    Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct);
}

public interface IChordRecognizer : IStageExecutor
{
    Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct);
}

public interface IModalAnalyzer
{
    /// <summary>Writes result.json into <see cref="StageContext.JobDir"/>.</summary>
    Task AnalyzeAsync(StageContext context, CancellationToken ct);
}
