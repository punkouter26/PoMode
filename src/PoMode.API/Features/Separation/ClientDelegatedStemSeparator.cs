using PoMode.API.Audio;
using PoMode.API.Features.Analysis;
using PoMode.API.Features.PitchTracking;
using PoMode.API.Pipeline;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Separation;

/// <summary>The vocal stem the browser separated, already validated and written into the job folder.</summary>
public sealed record ClientStems(string VocalsPath);

/// <summary>
/// Tier 2 separation: the user's browser runs UVR's MDX-Net Voc_FT
/// (<see cref="Infrastructure.ModelCatalog.MdxVocals"/>) on WebGPU and posts the vocal stem to
/// <c>client-stems</c>. It exists for the host that cannot run HTDemucs — the Azure plan has neither
/// the memory nor the CPU — where the only other separator is the placeholder that copies the mix
/// into both stems, which is what put the mock-data banner on every hosted analysis.
///
/// <para>Parks exactly like <see cref="ClientDelegatedPitchTracker"/>, and a browser too slow to do
/// the work declines at once rather than letting the job wait out the timeout. Only the vocals
/// travel: MDX-Net's instrumental is by construction the mix minus its vocals, so it is derived here
/// from the upload, which halves what a browser has to send.</para>
/// </summary>
public sealed class ClientDelegatedStemSeparator(
    ClientWorkRegistry<ClientStems> registry,
    JobStore store,
    IAnalysisNotifier notifier,
    IConfiguration configuration,
    ILogger<ClientDelegatedStemSeparator> logger) : IStemSeparator
{
    /// <summary>Separation takes a browser minutes where pitch takes seconds, so it gets its own guard.</summary>
    private const int DefaultTimeoutSeconds = 900;

    public string Name => nameof(ClientDelegatedStemSeparator);
    public ExecutionTier Tier => ExecutionTier.ClientDelegated;

    public Task<bool> IsAvailableAsync(CancellationToken ct)
        => Task.FromResult(configuration.GetValue("Tier2:Enabled", defaultValue: true));

    public async Task SeparateAsync(StageContext context, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(
            configuration.GetValue("Tier2:SeparationTimeoutSeconds", DefaultTimeoutSeconds));

        // Register before announcing, so a very fast browser cannot post before anyone is waiting.
        var wait = registry.WaitAsync(context.JobId, timeout, ct);
        await ClientDelegatedPitchTracker.AnnounceAsync(store, notifier, context.JobId, ct);
        logger.LogInformation("Job {JobId} is awaiting browser stem separation.", context.JobId);
        var stems = await wait;

        var vocals = AudioDecoder.Decode(stems.VocalsPath);
        var mix = Stereo(AudioDecoder.Decode(context.InputPath), vocals.SampleRate);
        var instrumental = new float[vocals.Samples.Length];
        for (var i = 0; i < instrumental.Length; i++)
        {
            instrumental[i] = (i < mix.Length ? mix[i] : 0f) - vocals.Samples[i];
        }
        WavWriter.Write(Path.Combine(context.JobDir, "instrumental.wav"), new AudioBuffer(instrumental, vocals.SampleRate, 2));
        logger.LogInformation("Job {JobId} received browser-separated vocals ({Seconds:0.0} s).",
            context.JobId, vocals.DurationSeconds);
    }

    /// <summary>The upload as interleaved stereo at <paramref name="sampleRate"/>, each channel
    /// resampled on its own because the band-limited resampler takes one channel at a time.</summary>
    private static float[] Stereo(AudioBuffer mix, int sampleRate)
    {
        var frames = mix.Samples.Length / mix.Channels;
        var channels = new float[2][];
        for (var channel = 0; channel < 2; channel++)
        {
            var source = Math.Min(channel, mix.Channels - 1);
            var mono = new float[frames];
            for (var frame = 0; frame < frames; frame++)
            {
                mono[frame] = mix.Samples[(frame * mix.Channels) + source];
            }
            channels[channel] = AudioDecoder.ResampleBandLimited(new AudioBuffer(mono, mix.SampleRate, 1), sampleRate).Samples;
        }

        var interleaved = new float[channels[0].Length * 2];
        for (var frame = 0; frame < channels[0].Length; frame++)
        {
            interleaved[2 * frame] = channels[0][frame];
            interleaved[(2 * frame) + 1] = channels[1][frame];
        }
        return interleaved;
    }
}
