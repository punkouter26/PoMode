using System.Text.RegularExpressions;

namespace PoMode.API.Features.Reference;

/// <summary>
/// Turns an uploaded file's name into something worth asking a music catalogue about.
///
/// <para>Pure and separate from the network client so the guessing can be tested without reaching
/// MusicBrainz, and because the guess is the part that decides whether the lookup is useful at all:
/// "04_So_What_(Official_Audio)_320kbps.mp3" and "So What" are the same recording, but only one of
/// them is a search.</para>
///
/// <para>It also knows when <em>not</em> to ask. Audio this app generated itself — a Mode Lab render,
/// a hum take, a live take — has a name describing a setting, not a release, and searching for it
/// would return a confident match for a recording the user never uploaded. Saying "this is not a
/// catalogue recording" is the correct answer there, and a far better one than a wrong artist.</para>
/// </summary>
public static partial class ReferenceQuery
{
    /// <summary>Filenames this app writes itself. Prefix-matched, case-insensitively.</summary>
    private static readonly string[] GeneratedPrefixes = ["live-take-", "hum_", "modelab_", "pomode_"];

    /// <summary>
    /// Words that mark a bracketed segment as packaging rather than part of the title. A bracket is
    /// dropped only when it contains one of these, so "(Live at Leeds)" and "(Reprise)" survive —
    /// those genuinely distinguish one recording from another in the catalogue.
    /// </summary>
    private static readonly string[] JunkWords =
    [
        "official", "video", "audio", "lyric", "lyrics", "visualizer", "visualiser",
        "hd", "hq", "4k", "1080p", "720p", "mv", "m/v", "explicit", "clean",
        "full album", "free download", "download", "youtube", "spotify",
    ];

    /// <summary>Trailing technical noise: bitrates, sample rates, and the like.</summary>
    [GeneratedRegex(@"\b\d{2,4}\s?(kbps|kbit|bpm|hz|khz)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalNoise();

    /// <summary>A leading track number: "04 ", "04. ", "04 - ", "04_".</summary>
    [GeneratedRegex(@"^\s*\d{1,3}\s*[-._)\]]?\s+")]
    private static partial Regex LeadingTrackNumber();

    [GeneratedRegex(@"[\(\[\{]([^\)\]\}]*)[\)\]\}]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex ExtraSpace();

    /// <summary>Below this many characters there is nothing for a catalogue to match on.</summary>
    private const int MinQueryLength = 3;

    /// <summary>
    /// True when PoMode wrote this file itself — a Mode Lab render, a hum take, a live take.
    ///
    /// <para>Separate from <see cref="FromFileName"/> returning null because the two absences want
    /// different words in front of the user. "We made this, there is no release to compare it to" is
    /// a complete answer; "there was nothing searchable in the name" is an invitation to type one.
    /// </para>
    /// </summary>
    public static bool IsAppGenerated(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }
        var name = Path.GetFileName(fileName.Trim());
        return GeneratedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The catalogue query for a file name, or null when the file is not a catalogue recording —
    /// either because this app generated it, or because nothing searchable survived the cleaning.
    /// </summary>
    public static string? FromFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || IsAppGenerated(fileName))
        {
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(Path.GetFileName(fileName.Trim()));
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        // Underscores and dots are separators in filenames and nowhere else; hyphens are left alone
        // because "Artist - Title" is the commonest shape a download arrives in and MusicBrainz reads
        // it correctly as a free-text query.
        var cleaned = stem.Replace('_', ' ').Replace('.', ' ');
        cleaned = Bracketed().Replace(cleaned, match =>
            JunkWords.Any(word => match.Groups[1].Value.Contains(word, StringComparison.OrdinalIgnoreCase))
                ? " "
                : match.Value);
        cleaned = TechnicalNoise().Replace(cleaned, " ");
        cleaned = LeadingTrackNumber().Replace(cleaned, "");
        cleaned = ExtraSpace().Replace(cleaned, " ").Trim(' ', '-', '–', '—');

        return cleaned.Length < MinQueryLength || !cleaned.Any(char.IsLetter) ? null : cleaned;
    }
}
