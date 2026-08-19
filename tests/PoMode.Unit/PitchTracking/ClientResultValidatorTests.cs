using PoMode.API.Features.PitchTracking;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.PitchTracking;

/// <summary>
/// These note events come from a browser, so they are untrusted input that flows straight into
/// notes.json, the modal engine and the MIDI writer. Every rule here exists to stop a broken or
/// hostile client turning that into a crash, a garbage analysis, or an unbounded artifact.
/// </summary>
public class ClientResultValidatorTests
{
    private const double TrackSeconds = 60.0;

    private static NoteEvent Note(
        int pitch = 60, double start = 1.0, double duration = 0.5, int velocity = 90)
        => new(pitch, start, duration, velocity);

    private static string? Validate(params NoteEvent[] notes)
        => ClientResultValidator.Validate(notes, TrackSeconds);

    [Fact]
    public void The_note_count_cap_is_enforced()
    {
        var notes = Enumerable.Range(0, 20_001).Select(_ => Note()).ToArray();

        var error = ClientResultValidator.Validate(notes, TrackSeconds);

        Assert.NotNull(error);
        Assert.Contains("too many", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The plain per-field range guards, each named in its own error message so a client
    /// author can tell which field it got wrong.</summary>
    [Fact]
    public void Out_of_range_pitch_start_and_duration_are_each_rejected_by_name()
    {
        Assert.Contains("pitch", Validate(Note(pitch: 109))!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start", Validate(Note(start: -0.001))!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duration", Validate(Note(duration: 0.0))!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_note_past_the_end_of_the_track_is_rejected()
    {
        // One second of slack absorbs honest rounding at the very end; a note far past the end is a
        // client that has lost track of time, and would put MIDI markers at nonsense bar numbers.
        Assert.Null(Validate(Note(start: TrackSeconds + 0.9)));

        var error = Validate(Note(start: TrackSeconds + 5));

        Assert.NotNull(error);
        Assert.Contains("start", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Non_finite_times_are_rejected()
    {
        // NaN silently defeats every comparison below, so it must be checked explicitly or it would
        // sail through and reach the JSON artifact and the MIDI writer.
        Assert.NotNull(Validate(Note(start: double.NaN)));
        Assert.NotNull(Validate(Note(duration: double.NaN)));
    }

    [Fact]
    public void The_whole_payload_is_rejected_rather_than_filtered()
    {
        // A partially-accepted analysis is worse than a rejected one: the user would get a result that
        // silently omits notes, with nothing saying so.
        var error = ClientResultValidator.Validate(
            [Note(), Note(pitch: 200), Note()], TrackSeconds);

        Assert.NotNull(error);
        Assert.Contains("pitch", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_track_duration_falls_back_to_the_decoder_cap()
    {
        // If the duration could not be read, times are still bounded — by the same 900 s ceiling the
        // upload path already enforces — rather than being left unchecked.
        Assert.Null(ClientResultValidator.Validate([Note(start: 800)], trackDurationSec: null));
        Assert.NotNull(ClientResultValidator.Validate([Note(start: 100_000)], trackDurationSec: null));
    }
}
