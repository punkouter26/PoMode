using System.Text.RegularExpressions;
using PoMode.Shared.Analysis;

namespace PoMode.Client.Services;

/// <summary>One recognised part of a library search, shown as a chip the person can remove.</summary>
public sealed record LibraryChip(string Label, string Token);

/// <summary>
/// The Library's one search box. It used to be a text box plus a key dropdown plus a mode dropdown;
/// now "dorian 120" means Dorian songs near 120 BPM. Each word is read as the first thing it can be:
/// a mode ("dorian", or three letters of one), a key ("D", "Eb", "f#"), a tempo ("120", or a range
/// "90-110"), a kind ("beat", "hum", "demo"); anything else has to appear in the file name.
/// </summary>
public sealed partial class LibraryQuery
{
    private static readonly string[] Sharps = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly string[] Flats = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>A tempo typed alone matches this far either side.</summary>
    public const double TempoSlack = 5;

    private readonly List<Func<LibraryEntryDto, bool>> _tests = [];
    private readonly List<LibraryChip> _chips = [];

    public string Text { get; }
    public IReadOnlyList<LibraryChip> Chips => _chips;

    private LibraryQuery(string text) => Text = text;

    public bool Matches(LibraryEntryDto entry) => _tests.All(test => test(entry));

    public static LibraryQuery Parse(string? text)
    {
        var query = new LibraryQuery(text ?? "");
        var chips = query._chips;
        var words = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            // Two-word modes ("minor pentatonic") are tried before either word alone.
            if (i + 1 < words.Length && ModeOf($"{word} {words[i + 1]}") is { } pair)
            {
                query.AddMode(pair, $"{word} {words[i + 1]}");
                i++;
            }
            else if (ModeOf(word) is { } mode)
            {
                query.AddMode(mode, word);
            }
            else if (PitchClassOf(word) is { } pitchClass)
            {
                chips.Add(new LibraryChip($"Key {Sharps[pitchClass]}", word));
                query._tests.Add(entry => PitchClassOf(entry.TonicName) == pitchClass);
            }
            else if (TempoRange().Match(word) is { Success: true } range)
            {
                var (low, high) = (double.Parse(range.Groups[1].Value, Invariant), double.Parse(range.Groups[2].Value, Invariant));
                (low, high) = (Math.Min(low, high), Math.Max(low, high));
                chips.Add(new LibraryChip($"{low:0}–{high:0} BPM", word));
                query._tests.Add(entry => entry.TempoBpm is { } bpm && bpm >= low && bpm <= high);
            }
            else if (Tempo().IsMatch(word) && double.Parse(word, Invariant) is >= 30 and <= 300 and var target)
            {
                chips.Add(new LibraryChip($"≈{target:0} BPM", word));
                query._tests.Add(entry => entry.TempoBpm is { } bpm && Math.Abs(bpm - target) <= TempoSlack);
            }
            else if (KindOf(word) is { } kind)
            {
                chips.Add(new LibraryChip(kind.Label, word));
                query._tests.Add(entry => entry.Origin?.Kind == kind.Kind);
            }
            else
            {
                var name = word;
                query._tests.Add(entry => entry.FileName.Contains(name, StringComparison.OrdinalIgnoreCase));
            }
        }
        return query;
    }

    /// <summary>The text with one chip's words taken out: what removing that chip leaves.</summary>
    public string Without(LibraryChip chip)
    {
        var index = Text.IndexOf(chip.Token, StringComparison.Ordinal);
        return index < 0 ? Text : string.Join(' ', (Text[..index] + Text[(index + chip.Token.Length)..])
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private void AddMode(ScaleMode mode, string token)
    {
        _chips.Add(new LibraryChip(ScaleModes.DisplayName(mode.ToString()), token));
        _tests.Add(entry => string.Equals(entry.PrimaryMode, mode.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static ScaleMode? ModeOf(string word)
    {
        if (word.Length < 3)
        {
            return null;
        }
        foreach (var mode in Enum.GetValues<ScaleMode>())
        {
            var name = ScaleModes.DisplayName(mode.ToString());
            // A one-word mode also answers to its first three letters or more ("dor", "mixo").
            if (name.Equals(word, StringComparison.OrdinalIgnoreCase)
                || (!name.Contains(' ') && name.StartsWith(word, StringComparison.OrdinalIgnoreCase)))
            {
                return mode;
            }
        }
        return null;
    }

    public static int? PitchClassOf(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 2)
        {
            return null;
        }
        var normal = char.ToUpperInvariant(name[0]) + name[1..].Replace('♯', '#').Replace('♭', 'b');
        var index = Array.IndexOf(Sharps, normal);
        if (index < 0)
        {
            index = Array.IndexOf(Flats, normal);
        }
        return index < 0 ? null : index;
    }

    private static (string Kind, string Label)? KindOf(string word) => word.ToLowerInvariant() switch
    {
        "beat" or "beats" or "drums" => (TakeOrigin.Beat, "Beats"),
        "hum" or "hums" or "take" or "takes" => (TakeOrigin.HumTake, "Sung takes"),
        "demo" => (TakeOrigin.Demo, "Demo"),
        _ => null,
    };

    [GeneratedRegex(@"^(\d{2,3})\s*[-–]\s*(\d{2,3})$")]
    private static partial Regex TempoRange();

    [GeneratedRegex(@"^\d{2,3}$")]
    private static partial Regex Tempo();
}
