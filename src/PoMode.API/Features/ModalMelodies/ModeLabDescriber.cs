using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PoMode.API.Features.SongStatistics;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalMelodies;

/// <summary>
/// The Mode Lab set by describing what you want to hear: "darker and slower", "more Phrygian",
/// "funky, in D". The words are read first by plain rules, which cover nearly everything people
/// type and answer at once; only a phrase the rules find nothing in goes to a language model (the
/// same local or cloud interpreter the song summaries use), held to a JSON schema of the five
/// settings and nothing else. The settings themselves are the lab's own; this never invents one.
/// </summary>
public sealed partial class ModeLabDescriber(IEnumerable<ISongInterpreter> interpreters, ILogger<ModeLabDescriber> logger)
{
    /// <summary>The seven modes from brightest to darkest: "darker" moves one step down this.</summary>
    private static readonly ScaleMode[] Brightness =
        [ScaleMode.Lydian, ScaleMode.Ionian, ScaleMode.Mixolydian, ScaleMode.Dorian, ScaleMode.Aeolian, ScaleMode.Phrygian, ScaleMode.Locrian];

    private const string Schema =
        """{"type":"object","properties":{"mode":{"type":"string"},"key":{"type":"string"},"bpm":{"type":"number"},"style":{"type":"string"},"purity":{"type":"number"}}}""";

    public async Task<ModeLabDescriptionDto> DescribeAsync(string text, ModalMelodyRequest current, CancellationToken ct)
    {
        var (request, changes) = ByRules(text, current, Random.Shared.Next(1, 100000));
        if (changes.Count == 0)
        {
            (request, changes) = await ByModelAsync(text, current, ct);
        }
        var sentence = changes.Count == 0
            ? "Nothing in that matched a setting. Try words like darker, brighter, faster, slower, Dorian, in D, funky or smoother."
            : $"{char.ToUpperInvariant(changes[0][0])}{string.Join(", ", changes)[1..]}.";
        return new ModeLabDescriptionDto(request, sentence, changes.Count > 0);
    }

    /// <summary>The rules: each word that names a setting moves it, and says what it did.</summary>
    public static (ModalMelodyRequest Request, List<string> Changes) ByRules(string text, ModalMelodyRequest current, int newSeed)
    {
        var words = text.ToLowerInvariant();
        var request = current;
        var changes = new List<string>();
        var mode = Enum.GetValues<ScaleMode>().FirstOrDefault(m =>
            Regex.IsMatch(words, $@"\b{Regex.Escape(ScaleModes.DisplayName(m.ToString()).ToLowerInvariant())}\b"));
        var modeNamed = Enum.GetValues<ScaleMode>().Any(m =>
            Regex.IsMatch(words, $@"\b{Regex.Escape(ScaleModes.DisplayName(m.ToString()).ToLowerInvariant())}\b"));
        if (!modeNamed)
        {
            var steps = Count(words, "darker|sadder|moodier|gloomier") - Count(words, "brighter|happier|lighter|sunnier");
            if (steps != 0 && Array.IndexOf(Brightness, current.Mode) is >= 0 and var at)
            {
                mode = Brightness[Math.Clamp(at + steps, 0, Brightness.Length - 1)];
                modeNamed = mode != current.Mode;
            }
        }
        if (modeNamed && mode != current.Mode)
        {
            request = request with { Mode = mode };
            changes.Add($"{ScaleModes.DisplayName(mode.ToString())} instead of {ScaleModes.DisplayName(current.Mode.ToString())}");
        }

        if (KeyIn().Match(words) is { Success: true } key && PitchClassOf(key.Groups[1].Value) is { } named)
        {
            // "In D" names the home note of the mode being played, as a musician means it; the request
            // carries the parent key, so the mode's offset is taken back off.
            var parent = (((named - ScaleModes.ModeDegreeOffset(request.Mode)) % 12) + 12) % 12;
            if (parent != request.TonicPitchClass)
            {
                request = request with { TonicPitchClass = parent };
                changes.Add($"home note {ScaleModes.NoteName(named)}");
            }
        }

        if (BpmIn().Match(words) is { Success: true } bpmMatch
            && double.TryParse(bpmMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var bpm))
        {
            request = request with { Bpm = Math.Clamp(bpm, 50, 180) };
        }
        else
        {
            var faster = Count(words, "faster|quicker|upbeat|energetic") - Count(words, "slower|calmer|relaxed|laid.back");
            var much = Regex.IsMatch(words, @"\b(much|way|a lot)\b") ? 2 : 1;
            if (faster != 0)
            {
                request = request with { Bpm = Math.Clamp(current.Bpm + (faster * 15 * much), 50, 180) };
            }
        }
        if (Math.Abs(request.Bpm - current.Bpm) > 0.5)
        {
            changes.Add($"{request.Bpm:0} BPM");
        }

        MelodyStyle? style = words switch
        {
            _ when Regex.IsMatch(words, @"\b(funky|funkier|syncopated|offbeat|off-beat|groovy)\b") => MelodyStyle.Syncopated,
            _ when Regex.IsMatch(words, @"\b(arpeggio|arpeggios|arpeggiated|broken chords?)\b") => MelodyStyle.Arpeggiated,
            _ when Regex.IsMatch(words, @"\b(riff|repetitive|motif|motific|hook)\b") => MelodyStyle.Motific,
            _ when Regex.IsMatch(words, @"\b(smooth|smoother|lyrical|singable|flowing)\b") => MelodyStyle.Lyrical,
            _ => null,
        };
        if (style is { } picked && picked != current.Style)
        {
            request = request with { Style = picked };
            changes.Add($"{picked.ToString().ToLowerInvariant()} melody");
        }

        var purity = Count(words, "purer|stricter|more modal|clearer") - Count(words, "looser|freer|wander|less modal");
        if (purity != 0)
        {
            request = request with { TargetPurity = Math.Clamp(current.TargetPurity + (purity * 20), 0, 100) };
            if (Math.Abs(request.TargetPurity - current.TargetPurity) > 0.5)
            {
                changes.Add($"purity {request.TargetPurity:0}%");
            }
        }

        if (Regex.IsMatch(words, @"\b(new|another|different) (melody|tune|take)\b"))
        {
            request = request with { Seed = newSeed };
            changes.Add("a new melody");
        }
        return (request, changes);
    }

    private async Task<(ModalMelodyRequest, List<string>)> ByModelAsync(string text, ModalMelodyRequest current, CancellationToken ct)
    {
        foreach (var interpreter in interpreters.Where(i => !i.IsClassicFallback))
        {
            try
            {
                if (!await interpreter.IsAvailableAsync(ct))
                {
                    continue;
                }
                var prompt = new ChatPrompt(
                    "You turn a musician's description of how a loop should sound into settings. Reply with JSON "
                    + "holding only the settings the description asks to change: mode (one of Ionian, Dorian, "
                    + "Phrygian, Lydian, Mixolydian, Aeolian, Locrian, MajorPentatonic, MinorPentatonic), key (a "
                    + "note name such as D or Eb, the home note), bpm (50 to 180), style (Lyrical, Arpeggiated, "
                    + "Syncopated or Motific), purity (0 to 100, how strictly the melody keeps to its mode). "
                    + "Leave out anything the description does not ask for.",
                    [new ChatMessage("user", $"Current: {current.Mode}, {current.Bpm:0} BPM, {current.Style}, purity {current.TargetPurity:0}.\nDescription: {text}")],
                    Schema);
                var reply = new StringBuilder();
                await foreach (var chunk in interpreter.ReplyAsync(new InterpretationRequest(null!, text, prompt), ct))
                {
                    reply.Append(chunk);
                }
                return Apply(reply.ToString(), current);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogInformation(ex, "Interpreter {Interpreter} could not read a Mode Lab description.", interpreter.Name);
            }
        }
        return (current, []);
    }

    /// <summary>Applies a model's JSON reply, ignoring anything outside the settings' own ranges.</summary>
    internal static (ModalMelodyRequest, List<string>) Apply(string json, ModalMelodyRequest current)
    {
        var request = current;
        var changes = new List<string>();
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("mode", out var mode) && Enum.TryParse<ScaleMode>(mode.GetString(), true, out var m) && m != current.Mode)
        {
            request = request with { Mode = m };
            changes.Add($"{ScaleModes.DisplayName(m.ToString())} instead of {ScaleModes.DisplayName(current.Mode.ToString())}");
        }
        if (root.TryGetProperty("key", out var key) && PitchClassOf(key.GetString() ?? "") is { } named
            && (((named - ScaleModes.ModeDegreeOffset(request.Mode)) % 12) + 12) % 12 is var parent
            && parent != current.TonicPitchClass)
        {
            request = request with { TonicPitchClass = parent };
            changes.Add($"home note {ScaleModes.NoteName(named)}");
        }
        // A model often restates a setting it was shown; only what actually moves is a change.
        if (root.TryGetProperty("bpm", out var bpm) && bpm.TryGetDouble(out var b) && b is >= 50 and <= 180
            && Math.Abs(b - current.Bpm) > 0.5)
        {
            request = request with { Bpm = b };
            changes.Add($"{b:0} BPM");
        }
        if (root.TryGetProperty("style", out var style) && Enum.TryParse<MelodyStyle>(style.GetString(), true, out var s) && s != current.Style)
        {
            request = request with { Style = s };
            changes.Add($"{s.ToString().ToLowerInvariant()} melody");
        }
        if (root.TryGetProperty("purity", out var purity) && purity.TryGetDouble(out var p) && p is >= 0 and <= 100
            && Math.Abs(p - current.TargetPurity) > 0.5)
        {
            request = request with { TargetPurity = p };
            changes.Add($"purity {p:0}%");
        }
        return (request, changes);
    }

    private static int Count(string text, string alternatives)
        => Regex.Matches(text, $@"\b({alternatives})\b").Count;

    private static int? PitchClassOf(string name)
    {
        string[] sharps = ["c", "c#", "d", "d#", "e", "f", "f#", "g", "g#", "a", "a#", "b"];
        string[] flats = ["c", "db", "d", "eb", "e", "f", "gb", "g", "ab", "a", "bb", "b"];
        var lower = name.Trim().ToLowerInvariant().Replace('♯', '#').Replace('♭', 'b');
        var index = Array.IndexOf(sharps, lower);
        return index >= 0 ? index : Array.IndexOf(flats, lower) is >= 0 and var flat ? flat : null;
    }

    [GeneratedRegex(@"\b(?:in|key of)\s+([a-g](?:#|b|♯|♭)?)(?![a-z])")]
    private static partial Regex KeyIn();

    [GeneratedRegex(@"\b(\d{2,3})\s*bpm\b")]
    private static partial Regex BpmIn();
}
