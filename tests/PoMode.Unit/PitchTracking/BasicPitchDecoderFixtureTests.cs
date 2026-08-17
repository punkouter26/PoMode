using PoMode.API.Features.PitchTracking;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Unit.PitchTracking;

/// <summary>
/// Guards the C# side of spec §5's "specified once, implemented twice" contract: the committed
/// fixture was generated from <see cref="BasicPitchDecoder"/>, and this test proves the decoder
/// still reproduces it exactly. The JS port is held to the same file by
/// <c>PoMode.E2EUI.DecoderPortTests</c> — if either side drifts, one of the two tests names the note.
/// </summary>
public class BasicPitchDecoderFixtureTests
{
    [Fact]
    public void The_csharp_decoder_reproduces_the_shared_fixture_exactly()
    {
        var fixture = BasicPitchFixture.Load();

        var notes = BasicPitchDecoder.Decode(
            BasicPitchFixture.ToRectangular(fixture.Onsets),
            BasicPitchFixture.ToRectangular(fixture.Frames),
            fixture.FramesPerSecond,
            fixture.MinMidi);

        Assert.Equal(fixture.ExpectedNotes.Length, notes.Count);
        for (var i = 0; i < notes.Count; i++)
        {
            var expected = fixture.ExpectedNotes[i];
            var actual = notes[i];
            Assert.True(
                expected.MidiPitch == actual.MidiPitch
                && Math.Abs(expected.StartSec - actual.StartSec) < 1e-9
                && Math.Abs(expected.DurationSec - actual.DurationSec) < 1e-9
                && expected.Velocity == actual.Velocity,
                $"Note {i} diverged: expected {expected.MidiPitch} @ {expected.StartSec}s for " +
                $"{expected.DurationSec}s vel {expected.Velocity}, got {actual.MidiPitch} @ " +
                $"{actual.StartSec}s for {actual.DurationSec}s vel {actual.Velocity}.");
        }
    }
}
