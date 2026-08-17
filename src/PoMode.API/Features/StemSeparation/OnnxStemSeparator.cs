using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PoMode.API.Features.Audio;
using PoMode.API.Infrastructure;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.StemSeparation;

/// <summary>
/// Real vocal/instrumental separation via the HTDemucs v4 ONNX model on the CPU execution provider —
/// the model established by the 2026-08-16 stem-separation feasibility gate
/// (<c>.superpowers/spikes/2026-08-16-stem-model-feasibility.md</c>). Falls back to
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

    public string Name => nameof(OnnxStemSeparator);
    public ExecutionTier Tier => ExecutionTier.Local;

    public Task<bool> IsAvailableAsync(CancellationToken ct) =>
        Task.FromResult(!EnvironmentDetector.IsAzureHosted() && registry.IsDownloaded(ModelCatalog.HtDemucs));

    public async Task SeparateAsync(StageContext context, CancellationToken ct)
    {
        var modelPath = await registry.EnsureAsync(ModelCatalog.HtDemucs, ct);

        var buffer = AudioDecoder.Decode(context.InputPath);
        buffer = AudioConverter.ToStereo44100(buffer);

        // Deliberately lower than OnnxPitchTracker's ProcessorCount/2: at 6 threads (12-core machine)
        // this graph's per-thread scratch buffers pushed the process over available memory and
        // ONNX Runtime threw "bad allocation" mid-run on this machine (measured during Task 8, with
        // ~4.3 GB free at the time). 2 threads ran the real model reliably across repeated runs with
        // ~5.7 GB peak private memory — see the Task 8 report. Leaves the web host's own thread pool
        // and the rest of the API process comfortable headroom during a ~3-10 minute job.
        using var options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = 2 };
        using var session = new InferenceSession(modelPath, options);

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

        var totalFrames = buffer.Samples.Length / buffer.Channels;
        var left = new float[totalFrames];
        var right = new float[totalFrames];
        for (var i = 0; i < totalFrames; i++)
        {
            left[i] = buffer.Samples[i * 2];
            right[i] = buffer.Samples[(i * 2) + 1];
        }

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

        for (var chunkIndex = 0; chunkIndex < starts.Count; chunkIndex++)
        {
            ct.ThrowIfCancellationRequested();

            var chunkStart = starts[chunkIndex];
            var available = Math.Min(windowSamples, totalFrames - chunkStart);

            var inputTensor = new DenseTensor<float>(inputDims);
            for (var i = 0; i < available; i++)
            {
                inputTensor[0, 0, i] = left[chunkStart + i];
                inputTensor[0, 1, i] = right[chunkStart + i];
            }
            // Samples beyond `available` (the final, short chunk) stay zero — zero-padding the tail.

            var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, inputTensor) };
            using var results = session.Run(inputs, [outputName]);
            var stems = results[0].AsTensor<float>();

            for (var i = 0; i < available; i++)
            {
                var globalIndex = chunkStart + i;
                var w = crossfade[i];
                vocalsLeft[globalIndex] += stems[0, VocalsStemIndex, 0, i] * w;
                vocalsRight[globalIndex] += stems[0, VocalsStemIndex, 1, i] * w;
                weight[globalIndex] += w;
            }

            logger.LogInformation(
                "Stem separation: chunk {ChunkIndex} of {ChunkCount} complete (job {JobId}).",
                chunkIndex + 1, starts.Count, context.JobId);
        }

        var vocalsInterleaved = new float[totalFrames * 2];
        var instrumentalInterleaved = new float[totalFrames * 2];
        for (var i = 0; i < totalFrames; i++)
        {
            var w = weight[i];
            var vocalLeft = w > 0 ? vocalsLeft[i] / w : 0f;
            var vocalRight = w > 0 ? vocalsRight[i] / w : 0f;

            vocalsInterleaved[i * 2] = vocalLeft;
            vocalsInterleaved[(i * 2) + 1] = vocalRight;
            instrumentalInterleaved[i * 2] = Math.Clamp(left[i] - vocalLeft, -1f, 1f);
            instrumentalInterleaved[(i * 2) + 1] = Math.Clamp(right[i] - vocalRight, -1f, 1f);
        }

        WavWriter.Write(Path.Combine(context.JobDir, "vocals.wav"), new AudioBuffer(vocalsInterleaved, buffer.SampleRate, 2));
        WavWriter.Write(Path.Combine(context.JobDir, "instrumental.wav"), new AudioBuffer(instrumentalInterleaved, buffer.SampleRate, 2));
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
