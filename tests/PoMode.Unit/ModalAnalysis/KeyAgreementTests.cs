using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

/// <summary>Essentia's major/minor reading against the mode: the parent key and its relative minor
/// are the same seven notes with another home, so they read as agreement, not as a clash.</summary>
public class KeyAgreementTests
{
    [Fact]
    public void Parent_key_and_relative_minor_agree_and_a_different_note_set_disagrees()
    {
        // F Lydian: parent key C major, relative minor A minor.
        var lydian = new ModalResult(1, 5, "F", 0.9, ScaleMode.Lydian, 0.9, 84, false, []);

        Assert.Contains("the same home note", KeyAgreement.Describe(lydian, 5, major: true, 0.7));
        Assert.Contains("parent key of F Lydian", KeyAgreement.Describe(lydian, 0, major: true, 0.7));
        Assert.Contains("relative minor", KeyAgreement.Describe(lydian, 9, major: false, 0.7));
        Assert.Contains("disagree", KeyAgreement.Describe(lydian, 2, major: true, 0.7));
        Assert.Contains("named no mode", KeyAgreement.Describe(lydian with { PrimaryMode = null }, 0, true, 0.7));
    }
}
