using PoMode.API.Audio;
using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.VoiceProfile;

/// <summary>
/// Measures a job's intonation once and keeps it as <c>intonation.json</c>. Unlike <c>/stats</c>,
/// which is arithmetic over stored artifacts, this reads the audio again — a second or two for a full
/// song — so it is cached like a measurement rather than recomputed per request like a statistic.
/// The cache records how many notes it measured and against which tuning, so a notes.json rewritten
/// later (a browser-tier result arriving) or a changed reference is measured afresh instead of being
/// graded against notes or a pitch that no longer apply.
/// </summary>
public sealed class VoiceProfileService(JobStore store, ILogger<VoiceProfileService> logger)
{
    private const string ArtifactName = "intonation.json";

    /// <summary>TuningCents is nullable so a cache written before it existed never passes for one
    /// measured against a known reference.</summary>
    private sealed record IntonationArtifact(int NoteCount, double? TuningCents, List<NoteIntonation> Notes);

    public async Task<IReadOnlyList<NoteIntonation>> IntonationAsync(JobState state, CancellationToken ct)
    {
        var notes = await store.ReadArtifactListAsync<NoteEvent>(state.JobId, "notes.json", ct);
        if (notes.Count == 0)
        {
            return [];
        }
        // A song is graded against its own tuning: the band sets the pitch, and a singer in tune with a
        // band tuned 15 cents flat is in tune. A hum take was sung over this app's backing at A=440, so
        // that is its reference — correcting for the take's own offset would forgive exactly the
        // flatness against the chords that a singer wants to hear about.
        var tuning = state.Origin?.Kind == TakeOrigin.HumTake
            ? 0.0
            : (await store.ReadArtifactAsync<ModalResult>(state.JobId, "result.json", ct))?.TuningOffsetCents ?? 0.0;
        if (await store.ReadArtifactAsync<IntonationArtifact>(state.JobId, ArtifactName, ct) is { } cached
            && cached.NoteCount == notes.Count
            && cached.TuningCents == tuning)
        {
            return cached.Notes;
        }

        // The separated vocal when there is one — the full mix would grade the singer against the bass.
        var audioPath = await store.GetArtifactPathAsync(state.JobId, "vocals.wav", ct)
                        ?? await store.GetArtifactPathAsync(state.JobId, Path.GetFileName(store.InputPath(state)), ct);
        if (audioPath is not null)
        {
            try
            {
                var measured = await Task.Run(
                    () => IntonationMeter.Measure(AudioDecoder.Decode(audioPath), notes, tuning), ct);
                await store.WriteArtifactAsync(state.JobId, ArtifactName, new IntonationArtifact(notes.Count, tuning, [.. measured]), ct);
                return measured;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not cached: an undecodable file today may be a restored one tomorrow.
                logger.LogWarning(ex, "Could not measure intonation for job {JobId}; judging from note times only.", state.JobId);
            }
        }
        return [.. notes.Select(n => new NoteIntonation(n.MidiPitch, n.DurationSec, null, null))];
    }
}
