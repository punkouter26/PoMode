using PoMode.API.Features.VoiceProfile;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.VoiceProfile;

public class VoiceProfilerTests
{
    /// <summary>
    /// A line sung evenly across <paramref name="lowMidi"/>..<paramref name="highMidi"/>, three times
    /// over, lands on the expected type (or declines with too few notes), names a neighbouring type
    /// only when the voice genuinely sits between two, and picks as its strongest note the one sung in
    /// tune and steady over pitches sung just as long but 20 cents out.
    /// </summary>
    [Theory]
    [InlineData(46, 64, VoiceType.Baritone, null)]     // A#2–E4, centred on G3
    [InlineData(64, 79, VoiceType.Soprano, null)]      // E4–G5
    [InlineData(49, 63, VoiceType.Baritone, "Tenor")]  // C#3–D#4, centred on G#3: between the two
    [InlineData(55, 57, null, null)]                   // nine notes: too little to judge
    public void Voice_type_follows_where_the_singing_sits_and_the_strongest_note_is_the_best_held(
        int lowMidi, int highMidi, VoiceType? expected, string? leaning)
    {
        var tuned = (lowMidi + highMidi) / 2;
        var notes = Enumerable.Range(0, 3)
            .SelectMany(_ => Enumerable.Range(lowMidi, highMidi - lowMidi + 1))
            .Select(midi => midi == tuned
                ? new NoteIntonation(midi, 0.5, OffCents: 4, WobbleCents: 5)
                : new NoteIntonation(midi, 0.5, OffCents: -20, WobbleCents: 15))
            .ToList();

        var profile = VoiceProfiler.Build(notes, VoiceSubject.Song);

        Assert.Equal(expected, profile.Type);
        Assert.Equal(leaning, profile.Leaning);
        Assert.False(string.IsNullOrWhiteSpace(profile.Summary));
        if (expected is null)
        {
            Assert.Null(profile.StrongestMidi);
            Assert.Empty(profile.Notes);
            return;
        }
        Assert.Contains(profile.TypeLabel!.ToLowerInvariant(), profile.Summary);
        Assert.Equal(tuned, profile.StrongestMidi);
        Assert.Contains("closest to true pitch", profile.StrongestSentence);
        Assert.Contains("steadiest", profile.StrongestSentence);
        Assert.Contains(profile.Notes, note => note.Midi == tuned && note.OffCents == 4);
    }
}
