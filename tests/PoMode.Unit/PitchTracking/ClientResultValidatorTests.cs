using PoMode.API.Features.PitchTracking;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.PitchTracking;

public class ClientResultValidatorTests
{
    private const double TrackSeconds = 60.0;

    private static NoteEvent Note(int pitch = 60, double start = 1.0, double duration = 0.5, int velocity = 90)
        => new(pitch, start, duration, velocity);

    private static string? Validate(params NoteEvent[] notes)
        => ClientResultValidator.Validate(notes, TrackSeconds);

    [Fact]
    public void Cap_and_range_validation_rejects_malformed_notes()
    {
        var tooMany = Enumerable.Range(0, 20_001).Select(_ => Note()).ToArray();
        Assert.Contains("too many", ClientResultValidator.Validate(tooMany, TrackSeconds)!, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("pitch", Validate(Note(pitch: 109))!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("start", Validate(Note(start: -0.001))!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("duration", Validate(Note(duration: 0.0))!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrackBoundaries_and_non_finite_times_are_guarded()
    {
        Assert.Null(Validate(Note(start: TrackSeconds + 0.9)));
        Assert.NotNull(Validate(Note(start: TrackSeconds + 5)));

        Assert.Contains("finite", Validate(Note(start: double.NaN))!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finite", Validate(Note(duration: double.PositiveInfinity))!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Valid_and_empty_lists_pass_cleanly()
    {
        Assert.Null(Validate());
        Assert.Null(Validate(Note(60, 0.0, 0.5), Note(62, 0.5, 0.5)));
    }
}
