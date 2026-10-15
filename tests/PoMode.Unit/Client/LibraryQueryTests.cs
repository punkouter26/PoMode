using PoMode.Client.Services;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Client;

/// <summary>The Library's one search box: each word is read as the first thing it can be, shown back
/// as a removable chip, and every chip must hold for a row to match.</summary>
public class LibraryQueryTests
{
    private static LibraryEntryDto Song(string name, string? tonic, string? mode, double? bpm, string? kind = null)
        => new("0123456789abcdef0123456789abcdef", name, DateTimeOffset.UnixEpoch, JobStage.Complete, tonic, mode, bpm,
            kind is null ? null : new TakeOrigin(kind, ""));

    [Fact]
    public void Words_become_mode_key_tempo_kind_and_name_filters()
    {
        var dorian = Song("summer demo.wav", "D", "Dorian", 118);
        var lydian = Song("night drive.mp3", "Eb", "Lydian", 92);
        var beat = Song("beat.wav", null, null, null, TakeOrigin.Beat);

        var query = LibraryQuery.Parse("dor 120 summer");
        Assert.Equal(["Dorian", "≈120 BPM"], query.Chips.Select(chip => chip.Label));
        Assert.True(query.Matches(dorian));
        Assert.False(query.Matches(lydian));

        // A key by its flat or sharp spelling, a tempo range, and a kind.
        Assert.True(LibraryQuery.Parse("d# 90-100").Matches(lydian));
        Assert.Equal("Key D#", LibraryQuery.Parse("eb").Chips.Single().Label);
        Assert.True(LibraryQuery.Parse("beats").Matches(beat));
        Assert.False(LibraryQuery.Parse("beats").Matches(dorian));

        // Removing a chip takes its word out of the text and leaves the rest.
        var chip = query.Chips.First(c => c.Label == "Dorian");
        Assert.Equal("120 summer", query.Without(chip));
    }
}
