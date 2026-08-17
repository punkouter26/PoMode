using PoMode.Shared.Analysis;

namespace PoMode.API.Pipeline;

/// <summary>Per-job stage inputs. <paramref name="OnProgress"/> is an optional 0..1 in-stage
/// progress hint for long stages (stem separation); executors may ignore it.</summary>
public sealed record StageContext(string JobId, string JobDir, string InputPath, Action<double>? OnProgress = null);

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

    /// <summary>
    /// True for the Fake* development stand-ins that fabricate deterministic results instead of doing
    /// real work. A placeholder is always available, so without this flag one would outrank every
    /// executor of a higher tier — a browser genuinely running Basic Pitch (ClientDelegated) would
    /// lose to <c>FakePitchTracker</c> forever. Placeholders rank after every real free tier but
    /// still before Cloud: mock data is a better automatic fallback than silently spending money.
    /// </summary>
    bool IsPlaceholder => false;
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
