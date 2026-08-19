using PoMode.API.Features.Audio;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// The free classic-DSP pitch tracker: <see cref="YinMelodyTranscriber"/> behind the
/// <see cref="IPitchTracker"/> seam. Always available (no model to download), but ranked as a
/// classic fallback so it never displaces Basic Pitch (local or browser) — it exists so that when
/// no model can run, the stage produces real notes instead of <see cref="FakePitchTracker"/>'s
/// mock data.
/// </summary>
public sealed class YinPitchTracker : IPitchTracker, IFileTranscriber
{
    public string Name => nameof(YinPitchTracker);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool IsClassicFallback => true;
    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

    public Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct)
    {
        // Same input choice as OnnxPitchTracker: the separated vocal stem when it exists.
        var vocalsPath = Path.Combine(context.JobDir, "vocals.wav");
        return TranscribeFileAsync(File.Exists(vocalsPath) ? vocalsPath : context.InputPath, ct);
    }

    public Task<IReadOnlyList<NoteEvent>> TranscribeFileAsync(string audioPath, CancellationToken ct)
        => Task.Run(() => YinMelodyTranscriber.Transcribe(AudioDecoder.Decode(audioPath)), ct);
}
