using PoMode.API.Features.SongStatistics;
using Xunit;

namespace PoMode.Unit.SongStatistics;

/// <summary>
/// Guards the one place a language model's sloppiness can reach the reader: the split between the
/// plain-English summary and the theory one. Every case here is a shape a real model produced or
/// could plausibly produce.
/// </summary>
public class InterpretationPromptTests
{
    [Fact]
    public void Splits_on_the_exact_delimiter()
    {
        var (plain, theory) = InterpretationPrompt.Split(
            $"Everyone text.\n\n{InterpretationPrompt.Delimiter}\n\nMusician text.");

        Assert.Equal("Everyone text.", plain);
        Assert.Equal("Musician text.", theory);
    }

    /// <summary>
    /// The real failure that motivated tolerant matching: gemma4:26b wrote both halves correctly but
    /// typed "===FOR MUSICIating===". An exact match dumped the theory section, marker included,
    /// into the plain summary.
    /// </summary>
    [Fact]
    public void Splits_even_when_the_model_garbles_the_delimiter()
    {
        var (plain, theory) = InterpretationPrompt.Split(
            "Everyone text.\n\n===FOR MUSICIating===\n\nMusician text.");

        Assert.Equal("Everyone text.", plain);
        Assert.Equal("Musician text.", theory);
    }

    [Theory]
    [InlineData("===FOR MUSICIANS===")]
    [InlineData("=== FOR MUSICIANS ===")]
    [InlineData("---FOR MUSICIANS---")]
    [InlineData("FOR MUSICIANS")]
    [InlineData("For musicians:")]
    [InlineData("**FOR MUSICIANS**")]
    public void Accepts_the_marker_however_it_is_decorated(string marker)
    {
        var (plain, theory) = InterpretationPrompt.Split($"Everyone text.\n{marker}\nMusician text.");

        Assert.Equal("Everyone text.", plain);
        Assert.Equal("Musician text.", theory);
    }

    [Fact]
    public void Does_not_split_on_a_sentence_that_merely_begins_with_the_same_words()
    {
        // Long, undecorated, and plainly prose: eating this would delete half the answer.
        const string Raw = "Everyone text.\n"
            + "For musicians the notable feature is the natural sixth degree throughout the verse.";

        var (plain, theory) = InterpretationPrompt.Split(Raw);

        Assert.Equal(Raw, plain);
        Assert.Null(theory);
    }

    [Fact]
    public void Treats_a_response_with_no_delimiter_as_a_single_plain_summary()
    {
        var (plain, theory) = InterpretationPrompt.Split("Just the one summary.");

        Assert.Equal("Just the one summary.", plain);
        Assert.Null(theory);
    }

    [Fact]
    public void A_delimiter_with_nothing_after_it_is_not_two_summaries()
    {
        var (plain, theory) = InterpretationPrompt.Split(
            $"Everyone text.\n{InterpretationPrompt.Delimiter}\n   ");

        Assert.Equal("Everyone text.", plain);
        Assert.Null(theory);
    }

    [Fact]
    public void A_delimiter_with_nothing_before_it_promotes_the_remainder_to_the_plain_summary()
    {
        // Better one labelled-but-shown summary than an empty plain section over a full theory one.
        var (plain, theory) = InterpretationPrompt.Split(
            $"{InterpretationPrompt.Delimiter}\nMusician text.");

        Assert.Equal("Musician text.", plain);
        Assert.Null(theory);
    }

    [Fact]
    public void A_repeated_marker_does_not_fragment_the_theory_half()
    {
        var (plain, theory) = InterpretationPrompt.Split(
            $"Everyone text.\n{InterpretationPrompt.Delimiter}\nFirst.\n{InterpretationPrompt.Delimiter}\nSecond.");

        Assert.Equal("Everyone text.", plain);
        Assert.NotNull(theory);
        Assert.Contains("First.", theory, StringComparison.Ordinal);
        Assert.Contains("Second.", theory, StringComparison.Ordinal);
    }

    [Fact]
    public void The_delimiter_itself_never_survives_into_either_half()
    {
        var (plain, theory) = InterpretationPrompt.Split(
            $"Everyone text.\n{InterpretationPrompt.Delimiter}\nMusician text.");

        Assert.DoesNotContain("MUSICIANS", plain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("=", plain, StringComparison.Ordinal);
        Assert.NotNull(theory);
        Assert.DoesNotContain("=", theory, StringComparison.Ordinal);
    }

    [Fact]
    public void Handles_windows_line_endings()
    {
        var (plain, theory) = InterpretationPrompt.Split(
            $"Everyone text.\r\n{InterpretationPrompt.Delimiter}\r\nMusician text.");

        Assert.Equal("Everyone text.", plain);
        Assert.Equal("Musician text.", theory);
    }
}
