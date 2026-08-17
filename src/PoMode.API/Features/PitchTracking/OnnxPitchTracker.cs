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
public sealed class OnnxPitchTracker(ModelRegistry registry, ILogger<OnnxPitchTracker> logger) : IPitchTracker
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

    public string Name => nameof(OnnxPitchTracker);
    public ExecutionTier Tier => ExecutionTier.Local;

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(!EnvironmentDetector.IsAzureHosted() && registry.IsDownloaded(ModelCatalog.BasicPitch));

    public async Task<IReadOnlyList<NoteEvent>> TrackAsync(StageContext context, CancellationToken ct)
    {
        var modelPath = await registry.EnsureAsync(ModelCatalog.BasicPitch, ct);

        var vocalsPath = Path.Combine(context.JobDir, "vocals.wav");
        var audioPath = File.Exists(vocalsPath) ? vocalsPath : context.InputPath;

        var buffer = AudioDecoder.Decode(audioPath);
        buffer = AudioDecoder.ToMono(buffer);
        buffer = AudioDecoder.Resample(buffer, TargetSampleRate);

        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2) };
        using var session = new InferenceSession(modelPath, options);

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

        var samplesPerFrame = (double)windowSamples / outputFrames;
        var framesPerSecond = TargetSampleRate / samplesPerFrame;
        var overlapSamples = (int)Math.Round(Math.Min(OverlapFrames, outputFrames / 2.0) * samplesPerFrame);
        var hopSamples = Math.Max(1, windowSamples - overlapSamples);

        var starts = new List<int>();
        var totalSamples = buffer.Samples.Length;
        var start = 0;
        while (true)
        {
            starts.Add(start);
            if (start + windowSamples >= totalSamples)
            {
                break;
            }
            start += hopSamples;
        }

        var lastFrameOffset = (int)Math.Round(starts[^1] / samplesPerFrame);
        var totalFrames = lastFrameOffset + outputFrames;
        var onsetsGlobal = new float[totalFrames, pitchCount];
        var framesGlobal = new float[totalFrames, pitchCount];

        foreach (var windowStart in starts)
        {
            ct.ThrowIfCancellationRequested();

            var window = new float[windowSamples];
            var available = Math.Min(windowSamples, Math.Max(0, totalSamples - windowStart));
            if (available > 0)
            {
                Array.Copy(buffer.Samples, windowStart, window, 0, available);
            }

            var tensor = new DenseTensor<float>(window, [1, windowSamples, 1]);
            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

            using var results = session.Run(inputs, [NoteOutputName, OnsetOutputName]);
            var noteTensor = results[0].AsTensor<float>();
            var onsetTensor = results[1].AsTensor<float>();

            var frameOffset = (int)Math.Round(windowStart / samplesPerFrame);
            for (var f = 0; f < outputFrames; f++)
            {
                var globalFrame = frameOffset + f;
                if (globalFrame >= totalFrames)
                {
                    break;
                }
                for (var p = 0; p < pitchCount; p++)
                {
                    // Later windows overwrite earlier ones in the overlap zone — the simplest correct
                    // stitching strategy; see task-5-report.md for why this was chosen over Basic
                    // Pitch's own trim-and-concatenate overlap-add.
                    framesGlobal[globalFrame, p] = noteTensor[0, f, p];
                    onsetsGlobal[globalFrame, p] = onsetTensor[0, f, p];
                }
            }
        }

        return BasicPitchDecoder.Decode(onsetsGlobal, framesGlobal, framesPerSecond, MinMidi);
    }
}
