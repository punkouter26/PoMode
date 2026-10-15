using PoMode.API.Audio;
using PoMode.API.Features.Analysis;
using PoMode.API.Infrastructure;
using PoMode.Shared.Analysis;
using Whisper.net;

namespace PoMode.API.Features.Lyrics;

/// <summary>
/// The words of a song, read off its separated vocal by Whisper (base.en, through Whisper.net), and
/// timed, so the canvas can show the line being sung. Written once, in the background after a job
/// finishes (<see cref="JobAftercare"/>), as <c>lyrics.json</c>.
/// </summary>
/// <remarks>
/// Only from <c>vocals.wav</c>: on a full mix Whisper hears the band as words. A segment is kept only
/// when Whisper was reasonably sure of it and it is speech rather than one of the bracketed sound
/// labels it writes for music ("[Music]", "(instrumental)"), because a confident line of invented
/// lyrics under a song is worse than none. English only, as the model is.
/// </remarks>
public sealed class LyricsTranscriber(JobStore store, ModelRegistry models, ILogger<LyricsTranscriber> logger)
{
    public const string ArtifactName = "lyrics.json";

    /// <summary>Whisper's own average token probability for a segment, below which it is dropped.</summary>
    private const float MinProbability = 0.4f;

    private const int WhisperRate = 16000;

    /// <summary>The lines, or null when there was no separated vocal to read.</summary>
    public async Task<IReadOnlyList<LyricLineDto>?> TranscribeAsync(JobState job, CancellationToken ct)
    {
        if (await store.GetArtifactPathAsync(job.JobId, "vocals.wav", ct) is not { } vocals)
        {
            return null;
        }
        var modelPath = await models.EnsureAsync(ModelCatalog.WhisperBaseEn, ct);
        var audio = await Task.Run(() =>
            AudioDecoder.ResampleBandLimited(AudioDecoder.ToMono(AudioDecoder.Decode(vocals)), WhisperRate), ct);

        var lines = new List<LyricLineDto>();
        using var factory = WhisperFactory.FromPath(modelPath);
        // WithProbabilities: without it every segment reports 0, and the confidence floor drops them all.
        await using var processor = factory.CreateBuilder().WithLanguage("en").WithProbabilities().Build();
        await foreach (var segment in processor.ProcessAsync(audio.Samples, ct))
        {
            var text = segment.Text.Trim();
            if (text.Length == 0 || segment.Probability < MinProbability || IsSoundLabel(text))
            {
                continue;
            }
            lines.Add(new LyricLineDto(
                Math.Round(segment.Start.TotalSeconds, 2), Math.Round(segment.End.TotalSeconds, 2), text));
        }
        await store.WriteArtifactAsync(job.JobId, ArtifactName, lines, ct);
        logger.LogInformation("Read {Lines} lyric lines for job {JobId}.", lines.Count, job.JobId);
        return lines;
    }

    /// <summary>"[Music]", "(upbeat music)", "♪" and the like: Whisper describing sound, not words.</summary>
    internal static bool IsSoundLabel(string text)
        => text.Trim('♪', ' ') is var bare && (bare.Length == 0
            || (bare.StartsWith('[') && bare.EndsWith(']'))
            || (bare.StartsWith('(') && bare.EndsWith(')')));
}
