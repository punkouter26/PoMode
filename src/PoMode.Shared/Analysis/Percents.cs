namespace PoMode.Shared.Analysis;

/// <summary>
/// Percentages written into sentences, spelled the same way on every machine.
/// </summary>
/// <remarks>
/// The "P" format specifier is not one behaviour. .NET's own reference data puts a space before the
/// sign, and Windows' NLS and Linux's ICU disagree about whether it is there: the same
/// <c>value:P0</c> read "0%" on a developer machine and "0 %" on the CI runner, which failed a test
/// that asserts the sentence. A sentence the app builds has to read the same on the server, in the
/// browser and on every developer box, so the number and its percent sign are joined here instead.
/// </remarks>
public static class Percents
{
    /// <summary>
    /// A share of 1.0 as a whole number of percent: 0.42 is "42%". Rounds half away from zero, the
    /// way "P0" always has, so no figure moves when this replaces one.
    /// </summary>
    public static string Of(double fraction)
        => double.IsFinite(fraction)
            ? $"{Math.Round(fraction * 100, MidpointRounding.AwayFromZero):0}%"
            : "0%";

        /// <summary>
        /// The same for a confidence that may not have been measured. A null is nothing to print, which
        /// is what formatting a null double has always produced here.
        /// </summary>
        public static string Of(double? fraction) => fraction is { } value ? Of(value) : string.Empty;
}