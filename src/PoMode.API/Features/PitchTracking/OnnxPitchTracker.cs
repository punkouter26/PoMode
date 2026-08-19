using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PoMode.API.Features.Audio;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// Real note detection via the Spotify Basic Pitch ONNX model on the CPU execution provider
/// (the fastest and only collision-free EP measured on ARM64 in the 2026-08-16 spike, ~12.3 ms
/// per ~2s window). Falls back to <see cref="FakePitchTracker"/> automatically via
/// <see cref="ExecutionPlanner"/> when the model has not been downloaded yet.
/// </summary>
public sealed class OnnxPitchTracker(ModelRegistry registry, ILogger<OnnxPitchTracker> logger)
    : IPitchTracker, IFileTranscriber
{
    /// <summary>Basic Pitch's audio front end always expects 22.05 kHz mono (not exposed via ONNX metadata).</summary>
    private const int TargetSampleRate = 22050;

    /// <summary>
    /// 88 piano keys, A0..C8 (MIDI 21..108). This mapping is inherent to the model's architecture
    /// (bin 0 == the lowest piano key) and is not discoverable from ONNX tensor metadata.
    /// </summary>
    private const int MinMidi = 21;

    /// <summary>
    /// Basic Pitch's own overlap between consecutive inference windows (30 frames), mirrored here so
    /// windows don't miss onsets that fall near a window boundary. See
    /// <c>DEFAULT_OVERLAPPING_FRAMES</c> in the upstream <c>basic_pitch</c> Python package.
    /// </summary>
    private const int OverlapFrames = 30;

    /// <summary>
    /// The two 88-wide output tensors ("note"/frame-activation and "onset") are shape-identical, so
    /// they cannot be told apart from ONNX metadata alone. This name mapping was confirmed by reading
    /// the reference implementation's own ONNX inference call in <c>basic_pitch/inference.py</c>
    /// (<c>Model.predict</c>, <c>MODEL_TYPES.ONNX</c> branch), which explicitly requests
    /// <c>["StatefulPartitionedCall:1", "StatefulPartitionedCall:2", "StatefulPartitionedCall:0"]</c>
    /// and zips them with <c>["note", "onset", "contour"]</c>. Verified present (by name) against the
    /// live model's <see cref="InferenceSession.OutputMetadata"/> before use — see <see cref="TrackAsync"/>.
    /// </summary>
    private const string NoteOutputName = "StatefulPartitionedCall:1";
    private const string OnsetOutputName = "StatefulPartitionedCall:2";

    private readonly object _sessionGate = new();
    private (string Path, DateTime WrittenUtc)? _sessionModelKey;
    private InferenceSession? _session;

    public string Name => nameof(OnnxPitchTracker);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool UsesLocalModel => true;

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(!EnvironmentDetector.IsAzureHosted() && registry.IsDownloaded(ModelCatalog.BasicPitch));

    public Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct)
    {
        var vocalsPath = Path.Combine(context.JobDir, "vocals.wav");
        return TranscribeAsync(File.Exists(vocalsPath) ? vocalsPath : context.InputPath, ct);
    }

    /// <summary>
    /// Transcribes one audio file — the <see cref="IFileTranscriber"/> capability. Beyond
    /// <see cref="TrackAsync"/>, the pipeline also runs it on instrumental.wav to produce the
    /// backing-notes artifact (notes-backing.json).
    /// </summary>
    public Task<IReadOnlyList<NoteEvent>> TranscribeFileAsync(string audioPath, CancellationToken ct)
        => TranscribeAsync(audioPath, ct);

    /// <summary>Builds and caches the inference session ahead of the first job, so the first
    /// transcription doesn't pay model load and graph optimization on the user's clock.</summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        var modelPath = await registry.EnsureAsync(ModelCatalog.BasicPitch, ct);
        GetSession(modelPath);
    }

    public async Task<IReadOnlyList<NoteEvent>> TranscribeAsync(string audioPath, CancellationToken ct)
    {
        var modelPath = await registry.EnsureAsync(ModelCatalog.BasicPitch, ct);

        var buffer = AudioDecoder.Decode(audioPath);
        buffer = AudioDecoder.ToMono(buffer);
        buffer = AudioDecoder.Resample(buffer, TargetSampleRate);

        var session = GetSession(modelPath);

        var inputName = session.InputMetadata.Keys.Single();
        var inputDims = session.InputMetadata[inputName].Dimensions;
        var windowSamples = inputDims[1];

        if (!session.OutputMetadata.TryGetValue(NoteOutputName, out var noteMeta)
            || !session.OutputMetadata.TryGetValue(OnsetOutputName, out _))
        {
            throw new InvalidOperationException(
                $"Basic Pitch model at '{modelPath}' no longer exposes the expected output tensors " +
                $"'{NoteOutputName}'/'{OnsetOutputName}'. Observed outputs: " +
                string.Join(", ", session.OutputMetadata.Select(kv => $"{kv.Key}:[{string.Join(",", kv.Value.Dimensions)}]")));
        }

        var outputFrames = noteMeta.Dimensions[1];
        var pitchCount = noteMeta.Dimensions[2];

        logger.LogInformation(
            "Basic Pitch ONNX contract — input {InputName}: [{InputDims}]; note {NoteName}: [{NoteDims}]; " +
            "onset {OnsetName}: [{OnsetDims}]. Window={WindowSamples} samples, outputFrames={OutputFrames}, pitches={PitchCount}.",
            inputName, string.Join(",", inputDims), NoteOutputName, string.Join(",", noteMeta.Dimensions),
            OnsetOutputName, string.Join(",", session.OutputMetadata[OnsetOutputName].Dimensions),
            windowSamples, outputFrames, pitchCount);

        // Derived (not the canonical 256-sample FFT hop) so per-window frame arithmetic stays exact
        // for THIS pinned export: outputFrames * 256 = 44032 samples, 188 more than the model's actual
        // 43844-sample window, so anchoring on 256 would drift over many windows. samplesPerFrame is
        // self-consistent by construction (windowSamples / outputFrames spans exactly one window).
        // See task-5-report.md "Fix Round 1" for the full comparison.
        var samplesPerFrame = (double)windowSamples / outputFrames;
        var framesPerSecond = TargetSampleRate / samplesPerFrame;

        // Canonical Basic Pitch recipe: pad the front with half the overlap so a true t=0 onset lands
        // inside a window's reliable interior instead of on the model's zero-padded edge, then trim
        // half the overlap off both ends of every window's output before stitching — this discards the
        // "fake attack" edge frames entirely, so overlapping windows no longer need "later wins"
        // tie-breaking; their kept ranges tile the timeline contiguously by construction.
        var overlapFrames = Math.Min(OverlapFrames, outputFrames / 2);
        var trimFrames = overlapFrames / 2;
        var overlapSamples = (int)Math.Round(overlapFrames * samplesPerFrame);
        var hopSamples = Math.Max(1, windowSamples - overlapSamples);
        var leadingPadSamples = (int)Math.Round(trimFrames * samplesPerFrame);

        var audioSamples = buffer.Samples.Length;
        var padded = new float[leadingPadSamples + audioSamples];
        Array.Copy(buffer.Samples, 0, padded, leadingPadSamples, audioSamples);

        var starts = new List<int>();
        var start = 0;
        while (true)
        {
            starts.Add(start);
            if (start + windowSamples >= padded.Length)
            {
                break;
            }
            start += hopSamples;
        }

        // Sized to the real (unpadded) audio duration, not the last window's reach, so a zero-padded
        // trailing window can never push a note past the end of the actual clip.
        var totalFrames = Math.Max(1, (int)Math.Ceiling(audioSamples / samplesPerFrame));
        var onsetsGlobal = new float[totalFrames, pitchCount];
        var framesGlobal = new float[totalFrames, pitchCount];

        var window = new float[windowSamples];
        foreach (var windowStart in starts)
        {
            ct.ThrowIfCancellationRequested();

            var available = Math.Min(windowSamples, Math.Max(0, padded.Length - windowStart));
            if (available > 0)
            {
                Array.Copy(padded, windowStart, window, 0, available);
            }
            if (available < windowSamples)
            {
                Array.Clear(window, available, windowSamples - available);
            }

            var tensor = new DenseTensor<float>(window, [1, windowSamples, 1]);
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

            using var results = session.Run(inputs, [NoteOutputName, OnsetOutputName]);
            var noteTensor = results[0].AsTensor<float>();
            var onsetTensor = results[1].AsTensor<float>();

            // frameOffset is this window's frame-0 position in original (unpadded) audio time; adding
            // the trimmed local frame index f lands each kept frame at its true global position.
            var frameOffset = (int)Math.Round((windowStart - leadingPadSamples) / samplesPerFrame);
            var lastFrame = outputFrames - trimFrames;
            for (var f = trimFrames; f < lastFrame; f++)
            {
                var globalFrame = frameOffset + f;
                if (globalFrame < 0 || globalFrame >= totalFrames)
                {
                    continue;
                }
                for (var p = 0; p < pitchCount; p++)
                {
                    framesGlobal[globalFrame, p] = noteTensor[0, f, p];
                    onsetsGlobal[globalFrame, p] = onsetTensor[0, f, p];
                }
            }
        }

        return BasicPitchDecoder.Decode(onsetsGlobal, framesGlobal, framesPerSecond, MinMidi);
    }

    private InferenceSession GetSession(string modelPath)
    {
        lock (_sessionGate)
        {
            // Keyed on the file EnsureAsync just validated — path plus write time, so a
            // re-downloaded or replaced model rebuilds the session instead of silently running
            // the stale one for the rest of the process lifetime.
            var key = (modelPath, File.GetLastWriteTimeUtc(modelPath));
            if (_session is null || _sessionModelKey != key)
            {
                _session?.Dispose();
                using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
                _session = new InferenceSession(modelPath, options);
                _sessionModelKey = key;
            }
            return _session;
        }
    }
}
