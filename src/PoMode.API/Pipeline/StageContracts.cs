using PoMode.Shared.Analysis;

namespace PoMode.API.Pipeline;

/// <summary>Per-job stage inputs. <paramref name="OnProgress"/> is an optional 0..1 in-stage
/// progress hint for long stages (stem separation); executors may ignore it.</summary>
public sealed record StageContext(string JobId, string JobDir, string InputPath, Action<double>? OnProgress = null)
{
    private (string Path, Features.Audio.AudioBuffer Buffer)? _decodedAnalysisAudio;

    /// <summary>
    /// The audio the post-separation stages should analyze: the instrumental stem when separation
    /// produced one (vocal noise confuses beat and chord analysis), else the original upload.
    /// </summary>
    public string PreferredAnalysisPath
    {
        get
        {
            var instrumental = Path.Combine(JobDir, "instrumental.wav");
            return File.Exists(instrumental) ? instrumental : InputPath;
        }
    }

    /// <summary>
    /// Decoded <see cref="PreferredAnalysisPath"/>, cached per path so back-to-back consumers in
    /// one stage (chord recognition, then the beat grid) pay for a single decode. The cache pins
    /// the whole PCM buffer, so the pipeline drops it via <see cref="ReleaseAnalysisAudio"/> as
    /// soon as the stage that used it completes. Single-threaded by construction: stages run
    /// sequentially for a job.
    /// </summary>
    public Features.Audio.AudioBuffer DecodePreferredAnalysisAudio()
    {
        var path = PreferredAnalysisPath;
        if (_decodedAnalysisAudio is not { } cached || cached.Path != path)
        {
            _decodedAnalysisAudio = (path, Features.Audio.AudioDecoder.Decode(path));
        }
        return _decodedAnalysisAudio.Value.Buffer;
    }

    public void ReleaseAnalysisAudio() => _decodedAnalysisAudio = null;

    private double? _tuningOffsetCents;

    /// <summary>
    /// How far this recording sits from A=440, in cents, measured once and reused by every stage
    /// that turns a frequency into a note. Zero when the deviations did not cluster tightly enough
    /// for one number to describe the recording.
    ///
    /// <para>Cached because it is a property of the song, not of a stage: the note transcriber and
    /// the chroma extractor must correct by the same amount or they will disagree about what note
    /// was played. A failure here is never fatal — an unmeasurable offset simply means no
    /// correction, which is the behaviour the pipeline had before.</para>
    /// </summary>
    public double TuningOffsetCents(ILogger? logger = null)
    {
        if (_tuningOffsetCents is { } cached)
        {
            return cached;
        }

        try
        {
            var estimate = Features.Audio.TuningEstimator.Estimate(DecodePreferredAnalysisAudio());
            _tuningOffsetCents = estimate.AppliedCents;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Tuning estimation failed for job {JobId}; analysing at A=440.", JobId);
            _tuningOffsetCents = 0.0;
        }

        return _tuningOffsetCents.Value;
    }
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

    /// <summary>
    /// True for the free, model-less classic-DSP alternatives (YIN pitch, Viterbi chord decoding).
    /// They do real work, so they beat placeholders — but they rank after every model-backed free
    /// tier (local ONNX, browser inference), so adding them never changes which executor a stage
    /// runs by default. They exist to replace mock data with real math when no model can run.
    /// </summary>
    bool IsClassicFallback => false;

    /// <summary>
    /// True when this executor runs a downloaded local model file (ONNX) rather than plain code.
    /// Display metadata for the executor pickers — declared here, never inferred from class names.
    /// </summary>
    bool UsesLocalModel => false;
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

/// <summary>
/// Optional capability: transcribe an arbitrary audio file, outside the planned stage input. The
/// pipeline uses it for the best-effort backing-notes transcription, picking any registered pitch
/// tracker that offers it rather than binding to one concrete executor.
/// </summary>
public interface IFileTranscriber : IStageExecutor
{
    Task<IReadOnlyList<NoteEvent>> TranscribeFileAsync(string audioPath, CancellationToken ct);
}

public interface IChordRecognizer : IStageExecutor
{
    Task<IReadOnlyList<ChordSpan>> RecognizeAsync(StageContext context, CancellationToken ct);
}

