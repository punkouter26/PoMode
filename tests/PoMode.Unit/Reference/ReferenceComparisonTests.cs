using PoMode.API.Features.Reference;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Reference;

/// <summary>
/// Putting a community key and tempo estimate beside this app's own reading.
///
/// <para>The case worth most of these tests is the one that looks like a disagreement and is not: a
/// catalogue classifier can only answer major or minor, so a melody this app reads as D Dorian comes
/// back from it as F major. Those are the same seven notes with a different note treated as home,
/// which is the exact distinction the whole app exists to draw — reporting it as a plain mismatch
/// would throw away the most interesting thing the comparison produces.</para>
/// </summary>
public class ReferenceComparisonTests
{
    [Fact]
    public void The_same_home_note_and_the_same_third_is_agreement()
    {
        var (key, tempo, sentence) = ReferenceComparison.Compare(
            "A", ScaleMode.Aeolian, 120, referenceKey: "A", referenceScale: "minor", referenceBpm: 121);

        Assert.Equal(ReferenceAgreement.Agrees, key);
        Assert.Equal(ReferenceAgreement.Agrees, tempo);
        Assert.Contains("same home note", sentence);
    }

    [Fact]
    public void A_relative_reading_is_explained_rather_than_scored()
    {
        // The commonest real outcome: PoMode hears D Dorian, the catalogue's classifier says F major.
        var (key, _, sentence) = ReferenceComparison.Compare(
            "D", ScaleMode.Dorian, 100, referenceKey: "F", referenceScale: "major", referenceBpm: null);

        Assert.Equal(ReferenceAgreement.Differs, key);
        Assert.Contains("same seven", sentence);
        Assert.Contains("relative major", sentence);
        Assert.Contains("which note the music treats as home", sentence);
    }

    [Fact]
    public void A_relative_minor_reading_is_explained_the_same_way()
    {
        var (_, _, sentence) = ReferenceComparison.Compare(
            "C", ScaleMode.Ionian, 100, referenceKey: "A", referenceScale: "minor", referenceBpm: null);

        Assert.Contains("relative minor", sentence);
    }

    [Fact]
    public void The_same_tonic_with_a_different_third_is_a_real_disagreement()
    {
        // D major against D Dorian is not a relative reading — it is two different claims about the
        // same home note, and there is nothing to explain away.
        var (key, _, sentence) = ReferenceComparison.Compare(
            "D", ScaleMode.Dorian, 100, referenceKey: "D", referenceScale: "major", referenceBpm: null);

        Assert.Equal(ReferenceAgreement.Differs, key);
        Assert.Contains("disagree", sentence);
        Assert.DoesNotContain("relative", sentence);
    }

    [Fact]
    public void A_doubled_tempo_is_reported_as_one_pulse_counted_two_ways()
    {
        var (_, tempo, sentence) = ReferenceComparison.Compare(
            "C", ScaleMode.Ionian, 85, referenceKey: null, referenceScale: null, referenceBpm: 170);

        Assert.Equal(ReferenceAgreement.Differs, tempo);
        Assert.Contains("double", sentence);
    }

    [Fact]
    public void No_community_analysis_is_an_absence_not_a_disagreement()
    {
        var (key, tempo, sentence) = ReferenceComparison.Compare(
            "C", ScaleMode.Ionian, 120, referenceKey: null, referenceScale: null, referenceBpm: null);

        Assert.Equal(ReferenceAgreement.Unknown, key);
        Assert.Equal(ReferenceAgreement.Unknown, tempo);
        Assert.Contains("no community key or tempo analysis", sentence);
    }

    [Theory]
    [InlineData("Db", 1)]
    [InlineData("C#", 1)]
    [InlineData("Bb", 10)]
    [InlineData("A#", 10)]
    public void Flat_and_sharp_spellings_of_one_pitch_are_the_same_pitch(string name, int expected)
        => Assert.Equal(expected, ReferenceComparison.PitchClass(name));

    [Fact]
    public void Something_that_is_not_a_note_name_is_not_guessed_at()
        => Assert.Null(ReferenceComparison.PitchClass("H minor"));
}
