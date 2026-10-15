using PoMode.API.Features.Analysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Analysis;

/// <summary>"Songs like this": two songs are alike when they spend their time in the same modes, and
/// the sentence claims only what holds.</summary>
public class SongSimilarityTests
{
    private static ModalResult Song(int tonic, string tonicName, double bpm, params (ScaleMode Mode, double Seconds)[] windows)
    {
        var start = 0.0;
        var built = new List<ModalWindow>();
        foreach (var (mode, seconds) in windows)
        {
            built.Add(new ModalWindow(built.Count, start, start + seconds, "C", 1, 0, [], false, [new ModalMatch(mode, 0.9, [], [])]));
            start += seconds;
        }
        return new ModalResult(1, tonic, tonicName, 0.9, windows[0].Mode, 0.9, bpm, false, built);
    }

    [Fact]
    public void Songs_in_the_same_modes_score_higher_and_the_reason_states_only_what_holds()
    {
        var song = Song(2, "D", 100, (ScaleMode.Dorian, 30), (ScaleMode.Aeolian, 10));
        var twin = Song(2, "D", 104, (ScaleMode.Dorian, 20), (ScaleMode.Aeolian, 8));
        var other = Song(5, "F", 150, (ScaleMode.Lydian, 40));

        Assert.True(SongSimilarity.Score(song, twin) > SongSimilarity.MinScore);
        Assert.True(SongSimilarity.Score(song, other) < SongSimilarity.MinScore);
        Assert.True(SongSimilarity.Score(song, twin) > SongSimilarity.Score(song, other));

        Assert.Equal("Mostly Dorian too · 4 BPM apart · both on D", SongSimilarity.Reason(song, twin));
        // No shared main mode, no near tempo, no shared key: only the overlap is claimed.
        Assert.Equal("0% of their time in the same modes", SongSimilarity.Reason(song, other));
    }
}
