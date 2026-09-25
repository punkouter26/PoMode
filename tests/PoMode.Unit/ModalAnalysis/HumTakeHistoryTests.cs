using PoMode.API.Features.ModalMelodies;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

/// <summary>
/// The vocal range the Practice page reads back, and the key it offers to fit a mode into it. Two
/// things have to hold: with too little singing it says how much more is needed instead of guessing,
/// and with enough it ignores a stray octave slip at either end, placing the mode's home note half an
/// octave under where the voice centres.
/// </summary>
public class HumTakeHistoryTests
{
    /// <summary>
    /// One take: nineteen held notes spread evenly around <paramref name="centre"/>, plus one wild
    /// detection an octave and a half off at each end — the kind a pitch tracker produces when it
    /// slips, and exactly what a comfortable range must not be set by.
    /// </summary>
    private static IReadOnlyList<NoteEvent> Take(int centre)
        => [
            .. Enumerable.Range(-9, 19).Select(offset => new NoteEvent(centre + offset, offset + 9, 0.3, 90)),
            new NoteEvent(centre - 20, 20, 0.3, 90),
            new NoteEvent(centre + 20, 21, 0.3, 90),
        ];

    [Theory]
    // No takes, then two: the range needs three, so it says how many more rather than claiming one.
    [InlineData(0, 55, ScaleMode.Dorian, 3, null)]
    [InlineData(2, 55, ScaleMode.Dorian, 1, null)]
    // Centred on G3 (55): home goes to C#3, and C# Dorian lives in the key of B.
    [InlineData(3, 55, ScaleMode.Dorian, 0, 11)]
    // Centred on E4 (64): home on A#3, and A# Ionian is its own key.
    [InlineData(4, 64, ScaleMode.Ionian, 0, 10)]
    // The relative-minor pentatonic sits on the 6th of its parent: home C#, so the key of E.
    [InlineData(3, 55, ScaleMode.MinorPentatonic, 0, 4)]
    public void The_range_waits_for_enough_takes_then_fits_the_mode_home_under_the_voice(
        int takeCount, int centre, ScaleMode mode, int expectedTakesNeeded, int? expectedKey)
    {
        var takes = Enumerable.Range(0, takeCount).Select(_ => Take(centre)).ToList();

        var profile = HumTakeHistory.Profile(takes, mode);

        Assert.Equal(expectedTakesNeeded, profile.TakesNeeded);
        Assert.Equal(expectedKey, profile.Fit?.TonicPitchClass);
        if (expectedKey is null)
        {
            // Nothing is claimed from too little data — no range, no key, just what is needed.
            Assert.Null(profile.LowMidi);
            Assert.Null(profile.HighMidi);
            Assert.Contains($"{expectedTakesNeeded}", profile.Summary);
            return;
        }

        // The 10th and 90th percentiles land inside the sung spread, well clear of the slips at ±20.
        Assert.InRange(profile.LowMidi!.Value, centre - 9, centre - 7);
        Assert.InRange(profile.HighMidi!.Value, centre + 7, centre + 9);
        Assert.Equal(mode, profile.Fit!.Mode);
        Assert.Contains($"from {takeCount} takes", profile.Summary);
    }
}
