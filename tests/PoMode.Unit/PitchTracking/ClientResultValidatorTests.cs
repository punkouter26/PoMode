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
    public void A_plausible_payload_is_accepted()
        => Assert.Null(Validate(Note(), Note(pitch: 67, start: 2.0), Note(pitch: 72, start: 3.5)));

    [Fact]
    public void An_empty_payload_is_accepted_because_silence_really_has_no_notes()
        => Assert.Null(ClientResultValidator.Validate([], TrackSeconds));

    [Fact]
    public void Too_many_notes_are_rejected()
    {
        var flood = Enumerable.Range(0, 20_001).Select(_ => Note()).ToArray();

        var error = ClientResultValidator.Validate(flood, TrackSeconds);

        Assert.NotNull(error);
        Assert.Contains("too many", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exactly_the_limit_is_still_accepted()
    {
        var atLimit = Enumerable.Range(0, 20_000).Select(_ => Note()).ToArray();

        Assert.Null(ClientResultValidator.Validate(atLimit, TrackSeconds));
    }

    [Theory]
    [InlineData(20)]  // below the piano
    [InlineData(109)] // above it
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Pitches_outside_the_piano_range_are_rejected(int pitch)
    {
        var error = Validate(Note(pitch: pitch));

        Assert.NotNull(error);
        Assert.Contains("pitch", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(108)]
    public void The_range_boundaries_are_accepted(int pitch)
        => Assert.Null(Validate(Note(pitch: pitch)));

    [Fact]
    public void A_negative_start_time_is_rejected()
    {
        var error = Validate(Note(start: -0.001));

        Assert.NotNull(error);
        Assert.Contains("start", error, StringComparison.OrdinalIgnoreCase);
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

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void A_non_positive_duration_is_rejected(double duration)
    {
        var error = Validate(Note(duration: duration));

        Assert.NotNull(error);
        Assert.Contains("duration", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_absurdly_long_note_is_rejected()
    {
        var error = Validate(Note(duration: 31.0));

        Assert.NotNull(error);
        Assert.Contains("duration", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(-5)]
    public void Velocities_outside_midi_range_are_rejected(int velocity)
    {
        var error = Validate(Note(velocity: velocity));

        Assert.NotNull(error);
        Assert.Contains("velocity", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Non_finite_times_are_rejected(double value)
    {
        // NaN silently defeats every comparison below, so it must be checked explicitly or it would
        // sail through and reach the JSON artifact and the MIDI writer.
        Assert.NotNull(Validate(Note(start: value)));
        Assert.NotNull(Validate(Note(duration: value)));
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
    public void The_message_names_the_offending_note_so_a_client_author_can_fix_it()
    {
        var error = ClientResultValidator.Validate([Note(), Note(velocity: 0)], TrackSeconds);

        Assert.NotNull(error);
        Assert.Contains("1", error); // the index of the bad note
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
