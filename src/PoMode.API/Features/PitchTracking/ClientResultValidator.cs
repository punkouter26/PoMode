using PoMode.API.Features.Audio;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// Bounds-checks note events posted by a browser (spec §4 step 3: "the server validates the payload,
/// since it is client-supplied data").
///
/// <para>These notes flow straight into <c>notes.json</c>, the modal engine and the MIDI writer, so a
/// broken or hostile client must not be able to produce an unbounded artifact, a MIDI marker at bar
/// 10^9, or a <c>NaN</c> that defeats every downstream comparison.</para>
///
/// <para>The whole payload is rejected on the first bad note rather than filtering the bad ones out: a
/// partially-accepted analysis would silently omit notes with nothing telling the user.</para>
/// </summary>
public static class ClientResultValidator
{
    /// <summary>Well past any plausible vocal transcription; a flood beyond this is a broken client.</summary>
    private const int MaxNotes = 20_000;

    /// <summary>The 88-key piano. Basic Pitch itself cannot emit outside this.</summary>
    private const int MinMidiPitch = 21;
    private const int MaxMidiPitch = 108;

    private const double MaxNoteSeconds = 30.0;

    /// <summary>Absorbs honest rounding at the very end of a track.</summary>
    private const double EndSlackSeconds = 1.0;

    /// <summary>
    /// Returns null when the payload is acceptable, otherwise a message naming the problem and the
    /// offending note's index. <paramref name="trackDurationSec"/> may be null when the duration could
    /// not be read; times are then bounded by the same ceiling the upload path already enforces, so
    /// they are never left unchecked.
    /// </summary>
    public static string? Validate(IReadOnlyList<NoteEvent> notes, double? trackDurationSec)
    {
        if (notes.Count > MaxNotes)
        {
            return $"Too many notes: {notes.Count} were posted and the limit is {MaxNotes}.";
        }

        var latestStart = (trackDurationSec ?? AudioDecoder.MaxDurationSecondsDefault) + EndSlackSeconds;

        for (var index = 0; index < notes.Count; index++)
        {
            var note = notes[index];

            if (note.MidiPitch < MinMidiPitch || note.MidiPitch > MaxMidiPitch)
            {
                return $"Note {index} has MIDI pitch {note.MidiPitch}; the allowed range is {MinMidiPitch}-{MaxMidiPitch}.";
            }
            if (note.Velocity is < 1 or > 127)
            {
                return $"Note {index} has velocity {note.Velocity}; the allowed range is 1-127.";
            }
            // Checked before the comparisons below: NaN silently defeats every one of them.
            if (!double.IsFinite(note.StartSec) || !double.IsFinite(note.DurationSec))
            {
                return $"Note {index} has a start time or duration that is not a finite number.";
            }
            if (note.StartSec < 0 || note.StartSec > latestStart)
            {
                return $"Note {index} starts at {note.StartSec:0.###} s, outside the track (0-{latestStart:0.###} s).";
            }
            if (note.DurationSec <= 0 || note.DurationSec > MaxNoteSeconds)
            {
                return $"Note {index} has duration {note.DurationSec:0.###} s; it must be above 0 and at most {MaxNoteSeconds:0} s.";
            }
        }

        return null;
    }
}
