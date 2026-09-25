using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PoMode.Shared.Analysis;

/// <summary>What an interpreter is asked: a system message, the conversation, and the JSON schema
/// its reply must fit.</summary>
public sealed record ChatPrompt(string System, IReadOnlyList<ChatMessage> Messages, string Schema)
{
    /// <summary>
    /// Everything the caller put in front of the model — the measurements, and for a question the
    /// question and the conversation so far. A figure in the reply has to come from here.
    /// </summary>
    public string Shown => string.Join("\n", Messages.Where(m => m.Role == "user").Select(m => m.Content));

    /// <summary>
    /// The same request with the model's own reply sent back and the figures it invented named, for
    /// one more attempt. Naming them works where a bare retry does not: the model keeps its prose and
    /// only has to fix the numbers.
    /// </summary>
    public ChatPrompt Corrected(string reply, IReadOnlyList<string> unsupported) => this with
    {
        Messages =
        [
            .. Messages,
            new ChatMessage("assistant", reply),
            new ChatMessage("user",
                $"These figures in your reply are not in the measurements: {string.Join(", ", unsupported)}. "
                + "Rewrite the reply in the same JSON shape using only figures that appear in the measurements, "
                + "or say it in words."),
        ],
    };
}

public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Reads an interpreter's JSON reply while it is still being written, so each field's text can be
/// shown as it arrives instead of after the closing brace. Both runtimes that stream it — Ollama on
/// the server, the browser's built-in model on the client — use this one reader.
///
/// <para>Understands exactly the two reply schemas: one flat object of string and scalar fields.
/// Anything before the first quote (a code fence a model wrapped the object in) is skipped.</para>
/// </summary>
public sealed class ReplyReader
{
    private enum State { SeekKey, Key, SeekValue, Text, Scalar }

    private readonly Dictionary<string, StringBuilder> _fields = [];
    private readonly StringBuilder _key = new();
    private readonly StringBuilder _escape = new();
    private State _state;
    private StringBuilder _value = new();

    /// <summary>A field's text so far, or null if the reply has not reached it.</summary>
    public string? this[string field] => _fields.TryGetValue(field, out var value) ? value.ToString() : null;

    /// <summary>Feeds the next piece of the reply; returns the text each string field gained from it,
    /// in the order written.</summary>
    public List<(string Field, string Text)> Push(string chunk)
    {
        var gained = new List<(string Field, string Text)>();
        var delta = new StringBuilder();
        foreach (var character in chunk)
        {
            switch (_state)
            {
                case State.SeekKey:
                    if (character == '"')
                    {
                        _key.Clear();
                        _state = State.Key;
                    }
                    break;
                case State.Key:
                    if (Take(character, _key))
                    {
                        _state = State.SeekValue;
                    }
                    break;
                case State.SeekValue:
                    if (character == '"')
                    {
                        _fields[_key.ToString()] = _value = new StringBuilder();
                        _state = State.Text;
                    }
                    else if (character is not (':' or ' ' or '\t' or '\r' or '\n'))
                    {
                        _fields[_key.ToString()] = _value = new StringBuilder().Append(character);
                        _state = State.Scalar;
                    }
                    break;
                case State.Text:
                    var before = _value.Length;
                    var closed = Take(character, _value);
                    delta.Append(_value, before, _value.Length - before);
                    if (closed)
                    {
                        Flush(gained, delta);
                        _state = State.SeekKey;
                    }
                    break;
                case State.Scalar:
                    if (character is ',' or '}' || char.IsWhiteSpace(character))
                    {
                        _state = State.SeekKey;
                    }
                    else
                    {
                        _value.Append(character);
                    }
                    break;
            }
        }

        if (_state == State.Text)
        {
            Flush(gained, delta);
        }
        return gained;
    }

    private void Flush(List<(string Field, string Text)> gained, StringBuilder delta)
    {
        if (delta.Length > 0)
        {
            gained.Add((_key.ToString(), delta.ToString()));
            delta.Clear();
        }
    }

    /// <summary>One character of a JSON string; true at its closing quote. Escapes may arrive split
    /// across chunks, so a pending one is held until it is complete.</summary>
    private bool Take(char character, StringBuilder into)
    {
        if (_escape.Length > 0)
        {
            _escape.Append(character);
            if (_escape[1] != 'u')
            {
                into.Append(_escape[1] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', var other => other });
                _escape.Clear();
            }
            else if (_escape.Length == 6)
            {
                into.Append((char)int.Parse(_escape.ToString(2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                _escape.Clear();
            }
            return false;
        }

        switch (character)
        {
            case '\\':
                _escape.Append(character);
                return false;
            case '"':
                return true;
            default:
                into.Append(character);
                return false;
        }
    }
}

/// <summary>
/// Finds the figures in a model's reply that the measurements it was shown do not contain. The
/// prompt forbids adding numbers; this is what makes that rule more than a request.
///
/// <para>A written figure counts as measured when some measured figure rounds to it at the
/// precision it was written — "63%" quotes a measured 63.4, "65%" does not.</para>
/// </summary>
public static partial class GroundingCheck
{
    // ponytail: whole numbers up to 12 pass unchecked. Degrees, intervals and small counts are how
    // theory is spoken ("the 7th", "2 semitones", "4/4"), so checking them would reject good prose;
    // the ceiling is that a wrong small count ("3 chords" when 5 were measured) slips through.
    private const int FreeUpTo = 12;

    /// <summary>Unsupported figures in <paramref name="reply"/>, as written, each once.</summary>
    public static IReadOnlyList<string> Unsupported(string reply, string shown)
    {
        var measured = Figures().Matches(shown)
            .Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture))
            .ToHashSet();
        return [.. Figures().Matches(reply)
            .Select(match => match.Value)
            .Where(written => !Supported(written, measured))
            .Distinct()];
    }

    private static bool Supported(string written, HashSet<double> measured)
    {
        var value = double.Parse(written, CultureInfo.InvariantCulture);
        var dot = written.IndexOf('.', StringComparison.Ordinal);
        var decimals = dot < 0 ? 0 : Math.Min(15, written.Length - dot - 1);
        return (decimals == 0 && value <= FreeUpTo)
            || measured.Any(figure => Math.Round(figure, decimals, MidpointRounding.AwayFromZero) == value);
    }

    /// <summary>A number not glued to a letter or a point before it, so the octave in "C4" is part of
    /// a note name rather than a figure.</summary>
    [GeneratedRegex(@"(?<![\w.])\d+(?:\.\d+)?")]
    private static partial Regex Figures();
}
