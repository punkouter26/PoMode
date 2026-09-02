using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using PoMode.API.Features.Audio;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.StemSeparation;

/// <summary>
/// Real vocal/instrumental separation via the HTDemucs v4 ONNX model on the CPU execution provider —
/// the model established by the 2026-08-16 stem-separation feasibility gate. Falls back to
/// <see cref="FakeStemSeparator"/> automatically via <see cref="ExecutionPlanner"/> when the model has
/// not been downloaded yet.
///
/// HTDemucs consumes raw stereo waveform (no STFT/spectrogram front end) at a fixed, fully-static
/// input shape — the model cannot take arbitrary-length audio directly, so this class implements the
/// overlap-add chunking loop the model's own reference <c>infer.py</c> uses: window = the model's
/// declared sample count, stride = window - window/4, with a linear-ramp crossfade over the
/// window/4-sample overlap so chunk seams don't click. Peak private memory is ~5.7 GB (measured), so
/// this is always a background job stage, never interactive — see the Task 8 report for the measured
/// wall-clock timing (varies with machine load; roughly real-time to a few times real-time on the
/// reference dev machine, well down from the feasibility spike's more heavily-loaded ~3.4x estimate).
/// </summary>
public sealed class OnnxStemSeparator(ModelRegistry registry, ILogger<OnnxStemSeparator> logger) : IStemSeparator
{
    /// <summary>
    /// Stem order is fixed by the model's own training/export convention, not discoverable from ONNX
    /// metadata: <c>[drums, bass, other, vocals]</c>. Confirmed in the 2026-08-16 feasibility spike.
    /// </summary>
    private const int VocalsStemIndex = 3;

    /// <summary>How long the inference session survives after a job before its memory is released.
    /// Long enough that batch uploads and back-to-back songs skip the model load; short enough
    /// that an idle machine gets its RAM back.</summary>
    private static readonly TimeSpan SessionKeepAlive = TimeSpan.FromMinutes(10);

    private readonly object _sessionGate = new();
    private (string Path, DateTime WrittenUtc)? _sessionKey;
    private InferenceSession? _session;
    private Timer? _evictionTimer;

    public string Name => nameof(OnnxStemSeparator);
    public ExecutionTier Tier => ExecutionTier.Local;
    public bool UsesLocalModel => true;

    /// <summary>The cached session, rebuilt when the model file changes on disk. Mirrors
    /// OnnxPitchTracker's caching, plus idle eviction because this graph is ~350 MB of weights.</summary>
    private InferenceSession GetSession(string modelPath)
    {
        lock (_sessionGate)
        {
            _evictionTimer?.Dispose();
            _evictionTimer = null;
            var key = (modelPath, File.GetLastWriteTimeUtc(modelPath));
            if (_session is null || _sessionKey != key)
            {
                _session?.Dispose();
                // Deliberately lower than OnnxPitchTracker's ProcessorCount/2: at 6 threads
                // (12-core machine) this graph's per-thread scratch buffers pushed the process
                // over available memory and ONNX Runtime threw "bad allocation" mid-run — see the
                // Task 8 report. 2 threads ran the real model reliably (~5.7 GB peak).
                using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = 2 };
                _session = new InferenceSession(modelPath, options);
                _sessionKey = key;
            }
            return _session;
        }
    }

    /// <summary>Arms the idle release. Called after every run; any new run disarms it first.</summary>
    private void ScheduleSessionEviction()
    {
        lock (_sessionGate)
        {
            _evictionTimer?.Dispose();
            _evictionTimer = new Timer(_ =>
            {
                lock (_sessionGate)
                {
                    _session?.Dispose();
                    _session = null;
                    _sessionKey = null;
                    _evictionTimer?.Dispose();
                    _evictionTimer = null;
                }
            }, null, SessionKeepAlive, Timeout.InfiniteTimeSpan);
        }
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(!EnvironmentDetector.IsAzureHosted() && registry.IsDownloaded(ModelCatalog.HtDemucs));

    public async Task SeparateAsync(StageContext context, CancellationToken ct)
    {
        var modelPath = await registry.EnsureAsync(ModelCatalog.HtDemucs, ct);

        var buffer = AudioDecoder.Decode(context.InputPath);
        buffer = AudioConverter.ToStereo44100(buffer);

        var session = GetSession(modelPath);

        var inputName = session.InputMetadata.Keys.Single();
        var inputDims = session.InputMetadata[inputName].Dimensions.ToArray();
        var windowSamples = inputDims[^1];

        var outputName = session.OutputMetadata.Keys.Single();
        var outputDims = session.OutputMetadata[outputName].Dimensions.ToArray();
        var stemCount = outputDims[1];

        logger.LogInformation(
            "HTDemucs ONNX contract — input {InputName}: [{InputDims}]; output {OutputName}: [{OutputDims}].",
            inputName, string.Join(",", inputDims), outputName, string.Join(",", outputDims));

        if (stemCount <= VocalsStemIndex)
        {
            throw new InvalidOperationException(
                $"HTDemucs model at '{modelPath}' produced {stemCount} stem(s); expected at least " +
                $"{VocalsStemIndex + 1} so a vocals stem (index {VocalsStemIndex}) exists.");
        }

        // Mirrors the model's own reference infer.py: overlap is a quarter of the window, stride is
        // the remainder. Derived from live metadata (not hardcoded from the spike) per Task 8's
        // "trust the live metadata" instruction, so this adapts automatically if the pinned model file
        // is ever swapped for a variant with a different window size.
        var overlapSamples = windowSamples / 4;
        var strideSamples = windowSamples - overlapSamples;
        var crossfade = BuildCrossfadeWindow(windowSamples, overlapSamples);

        // The interleaved buffer is read directly (stride 2) instead of splitting into separate
        // channel arrays — that split alone cost ~106 MB of scratch on a five-minute song.
        var samples = buffer.Samples;
        var totalFrames = samples.Length / buffer.Channels;

        var starts = new List<int>();
        var start = 0;
        while (start < totalFrames)
        {
            starts.Add(start);
            if (start + windowSamples >= totalFrames)
            {
                break;
            }
            start += strideSamples;
        }

        // Overlap-add accumulators: weighted sum of the vocals stem per sample, plus the sum of
        // weights, so dividing recovers a true weighted average — a chunk covering a sample alone
        // (no overlap) divides out to exactly its own raw output; samples covered by two chunks get a
        // smooth linear-ramp blend instead of a hard, audible seam.
        var vocalsLeft = new float[totalFrames];
        var vocalsRight = new float[totalFrames];
        var weight = new float[totalFrames];

        // Row-major [1, 2, windowSamples]: the left channel occupies the first windowSamples floats,
        // the right channel the next windowSamples.
        var inputBuffer = new float[2 * windowSamples];
        using var inputValue = OrtValue.CreateTensorValueFromMemory(inputBuffer, Array.ConvertAll(inputDims, d => (long)d));
        using var runOptions = new RunOptions();
        string[] inputNames = [inputName];
        OrtValue[] inputValues = [inputValue];
        string[] outputNames = [outputName];

        for (var chunkIndex = 0; chunkIndex < starts.Count; chunkIndex++)
        {
            ct.ThrowIfCancellationRequested();

            var chunkStart = starts[chunkIndex];
            var available = Math.Min(windowSamples, totalFrames - chunkStart);

            for (var i = 0; i < available; i++)
            {
                inputBuffer[i] = samples[(chunkStart + i) * 2];
                inputBuffer[windowSamples + i] = samples[((chunkStart + i) * 2) + 1];
            }
            if (available < windowSamples)
            {
                // The final, short chunk: zero-pad the tail of each channel.
                Array.Clear(inputBuffer, available, windowSamples - available);
                Array.Clear(inputBuffer, windowSamples + available, windowSamples - available);
            }

            using var results = session.Run(runOptions, inputNames, inputValues, outputNames);
            var stems = results[0].GetTensorDataAsSpan<float>();

            // Row-major [1, stems, channels, samples]: base offsets of the vocals stem's two
            // channels, from the run's ACTUAL shape — declared metadata can carry -1 for a
            // dynamic axis, which would turn these offsets negative.
            var outputShape = results[0].GetTensorTypeAndShape().Shape;
            var outputSamples = (int)outputShape[^1];
            var vocalsLeftOffset = VocalsStemIndex * (int)outputShape[2] * outputSamples;
            var vocalsRightOffset = vocalsLeftOffset + outputSamples;

            for (var i = 0; i < available; i++)
            {
                var globalIndex = chunkStart + i;
                var w = crossfade[i];
                vocalsLeft[globalIndex] += stems[vocalsLeftOffset + i] * w;
                vocalsRight[globalIndex] += stems[vocalsRightOffset + i] * w;
                weight[globalIndex] += w;
            }

            logger.LogInformation(
                "Stem separation: chunk {ChunkIndex} of {ChunkCount} complete (job {JobId}).",
                chunkIndex + 1, starts.Count, context.JobId);
            context.OnProgress?.Invoke((chunkIndex + 1) / (double)starts.Count);
        }

        // The instrumental is written back into the decoded mix buffer in place — each element is
        // read (original) before it is overwritten (instrumental), so no third full-song array.
        var vocalsInterleaved = new float[totalFrames * 2];
        for (var i = 0; i < totalFrames; i++)
        {
            var w = weight[i];
            var vocalLeft = w > 0 ? vocalsLeft[i] / w : 0f;
            var vocalRight = w > 0 ? vocalsRight[i] / w : 0f;

            vocalsInterleaved[i * 2] = vocalLeft;
            vocalsInterleaved[(i * 2) + 1] = vocalRight;
            samples[i * 2] = Math.Clamp(samples[i * 2] - vocalLeft, -1f, 1f);
            samples[(i * 2) + 1] = Math.Clamp(samples[(i * 2) + 1] - vocalRight, -1f, 1f);
        }

        WavWriter.Write(Path.Combine(context.JobDir, "vocals.wav"), new AudioBuffer(vocalsInterleaved, buffer.SampleRate, 2));
        WavWriter.Write(Path.Combine(context.JobDir, "instrumental.wav"), new AudioBuffer(samples, buffer.SampleRate, 2));

        // Keep the session warm for the next song; the timer releases the memory when idle.
        ScheduleSessionEviction();
    }

    /// <summary>
    /// A window of 1s that linearly ramps 0→1 over the first <paramref name="overlapSamples"/> and
    /// 1→0 over the last <paramref name="overlapSamples"/>, flat at 1 in between. Used as an
    /// accumulation weight, not a final multiplier — see the overlap-add normalisation above.
    /// </summary>
    private static float[] BuildCrossfadeWindow(int windowSamples, int overlapSamples)
    {
        var window = new float[windowSamples];
        Array.Fill(window, 1f);
        if (overlapSamples <= 0)
        {
            return window;
        }
        for (var i = 0; i < overlapSamples; i++)
        {
            var ramp = (i + 1f) / (overlapSamples + 1f);
            window[i] = ramp;
            window[windowSamples - 1 - i] = ramp;
        }
        return window;
    }
}
