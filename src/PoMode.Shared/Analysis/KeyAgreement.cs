namespace PoMode.Shared.Analysis;

/// <summary>
/// How a second, independent key reading (Essentia's KeyExtractor, run in the browser on the mix)
/// stands against the modal analysis. Essentia knows only major and minor, so "different" is not
/// always disagreement: a Lydian song read as the major key a fourth below is the same seven notes
/// heard with another home, which is exactly the question a mode answers. Worded here, beside the
/// other musical statements, so the browser only reports what Essentia said.
/// </summary>
public static class KeyAgreement
{
    public static string Describe(ModalResult result, int pitchClass, bool major, double strength)
    {
        var reading = $"Essentia reads {ScaleModes.NoteName(pitchClass)} {(major ? "major" : "minor")} ({Percents.Of(strength)} sure)";
        if (result.PrimaryMode is not { } mode)
        {
            return $"{reading}. The modal analysis named no mode to compare it with.";
        }
        var parent = (((result.TonicPitchClass - ScaleModes.ModeDegreeOffset(mode)) % 12) + 12) % 12;
        var relativeMinor = (parent + 9) % 12;
        var modeName = ScaleModes.DisplayName(mode.ToString());
        if (pitchClass == result.TonicPitchClass)
        {
            return $"{reading}: the same home note as {result.TonicName} {modeName}.";
        }
        if (major && pitchClass == parent)
        {
            return $"{reading}, the parent key of {result.TonicName} {modeName}: the same seven notes, "
                + "heard with a different home. A major/minor reader cannot name a mode, so this is agreement.";
        }
        if (!major && pitchClass == relativeMinor)
        {
            return $"{reading}, the relative minor of the parent key of {result.TonicName} {modeName}: the same "
                + "seven notes, heard with a different home.";
        }
        return $"{reading}: a different set of notes from {result.TonicName} {modeName}. The two readings disagree here.";
    }
}
