using PoMode.API.Features.SongStatistics;
using Xunit;

namespace PoMode.Unit.SongStatistics;

public class InterpretationPromptTests
{
    [Fact]
    public void Splits_on_exact_and_garbled_delimiters()
    {
        var exact = InterpretationPrompt.Split($"Everyone text.\n\n{InterpretationPrompt.Delimiter}\n\nMusician text.");
        Assert.Equal("Everyone text.", exact.Plain);
        Assert.Equal("Musician text.", exact.Theory);

        var garbled = InterpretationPrompt.Split("Everyone text.\n\n===FOR MUSICIating===\n\nMusician text.");
        Assert.Equal("Everyone text.", garbled.Plain);
        Assert.Equal("Musician text.", garbled.Theory);
    }

    [Fact]
    public void Accepts_decorated_and_rule_markers()
    {
        var markers = new[]
        {
            "===FOR MUSICIANS===", "=== FOR MUSICIANS ===", "---FOR MUSICIANS---",
            "FOR MUSICIANS", "For musicians:", "**FOR MUSICIANS**", "===", "---", "___", "***"
        };

        foreach (var marker in markers)
        {
            var (plain, theory) = InterpretationPrompt.Split($"Everyone text.\n{marker}\nMusician text.");
            Assert.Equal("Everyone text.", plain);
            Assert.Equal("Musician text.", theory);
        }
    }

    [Fact]
    public void Keeps_theory_null_when_no_delimiter_is_present()
    {
        var (plain, theory) = InterpretationPrompt.Split("Just one plain block of text.");
        Assert.Equal("Just one plain block of text.", plain);
        Assert.Null(theory);
    }
}
