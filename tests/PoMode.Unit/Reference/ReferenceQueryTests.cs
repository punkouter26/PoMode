using PoMode.API.Features.Reference;
using Xunit;

namespace PoMode.Unit.Reference;

/// <summary>
/// Turning a file name into a catalogue search. The failure that matters is not a missed match — most
/// recordings are not in the catalogue anyway — but a confident match for the wrong song, which puts
/// someone else's key and tempo beside this user's analysis and invites them to believe it.
/// </summary>
public class ReferenceQueryTests
{
    [Theory]
    [InlineData("So What.mp3", "So What")]
    [InlineData("04_So_What.mp3", "So What")]
    [InlineData("04 - Miles Davis - So What.mp3", "Miles Davis - So What")]
    [InlineData("So What (Official Audio).mp3", "So What")]
    [InlineData("So What [HD] 320kbps.wav", "So What")]
    [InlineData("Miles.Davis.So.What.flac", "Miles Davis So What")]
    public void Packaging_is_stripped_and_the_title_survives(string fileName, string expected)
        => Assert.Equal(expected, ReferenceQuery.FromFileName(fileName));

    [Fact]
    public void A_bracket_that_distinguishes_the_recording_is_kept()
    {
        // "(Live at Leeds)" and "(Reprise)" are how the catalogue tells two recordings of one song
        // apart, so dropping every bracket would throw away the thing that identifies the match.
        Assert.Equal("My Generation (Live at Leeds)",
            ReferenceQuery.FromFileName("My Generation (Live at Leeds).mp3"));
        Assert.Equal("Overture (Reprise)", ReferenceQuery.FromFileName("Overture (Reprise).wav"));
    }

    [Theory]
    [InlineData("live-take-20260911-143000.wav")]
    [InlineData("Hum_Dorian_modal-dorian.wav")]
    [InlineData("ModeLab_Lydian_pop-axis.wav")]
    public void Audio_this_app_made_is_never_sent_to_a_catalogue(string fileName)
    {
        // Searching for these returns a confident match for a recording the user never uploaded.
        Assert.Null(ReferenceQuery.FromFileName(fileName));
        Assert.True(ReferenceQuery.IsAppGenerated(fileName));
    }

    [Theory]
    [InlineData("01.mp3")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void A_name_with_nothing_searchable_in_it_asks_nothing(string? fileName)
    {
        Assert.Null(ReferenceQuery.FromFileName(fileName));
        // And is distinguishable from the app's own output, because the page says something
        // different for each: one invites you to type a title, the other explains there is no release.
        Assert.False(ReferenceQuery.IsAppGenerated(fileName));
    }

    [Fact]
    public void A_path_is_reduced_to_its_file_name()
        => Assert.Equal("So What", ReferenceQuery.FromFileName(@"C:\Music\Kind of Blue\So What.mp3"));
}
