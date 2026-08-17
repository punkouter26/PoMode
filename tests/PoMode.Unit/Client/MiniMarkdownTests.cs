using PoMode.Client.Services;
using Xunit;

namespace PoMode.Unit.Client;

/// <summary>
/// The copilot's text comes from a language model, so it is untrusted input that ends up inside a
/// MarkupString. These tests pin down that escaping happens first and only a fixed inline subset is
/// ever turned back into markup.
/// </summary>
public class MiniMarkdownTests
{
    [Fact]
    public void Plain_text_becomes_a_paragraph()
        => Assert.Equal("<p>Hello there.</p>", MiniMarkdown.ToHtml("Hello there."));

    [Fact]
    public void Html_in_the_source_is_escaped_not_rendered()
    {
        var html = MiniMarkdown.ToHtml("<script>alert('x')</script>");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void An_img_onerror_payload_cannot_smuggle_an_attribute()
    {
        var html = MiniMarkdown.ToHtml("""<img src="x" onerror="alert(1)">""");

        // The words "onerror=" survive as escaped text, which is inert: what matters is that no tag
        // is opened and no quote is left unescaped, so nothing can ever become an attribute.
        Assert.DoesNotContain("<img", html);
        Assert.Contains("&lt;img", html);
        Assert.DoesNotContain("\"", html);
        Assert.Contains("onerror=&quot;", html);
    }

    [Fact]
    public void Quotes_and_ampersands_are_escaped()
    {
        var html = MiniMarkdown.ToHtml("""Bach & Sons said "hi" <yes>""");

        Assert.Contains("&amp;", html);
        Assert.Contains("&quot;", html);
        Assert.Contains("&lt;yes&gt;", html);
    }

    [Theory]
    [InlineData("**Dorian**", "<p><strong>Dorian</strong></p>")]
    [InlineData("*natural sixth*", "<p><em>natural sixth</em></p>")]
    [InlineData("the `0x6AD` mask", "<p>the <code>0x6AD</code> mask</p>")]
    public void The_supported_inline_subset_is_rendered(string source, string expected)
        => Assert.Equal(expected, MiniMarkdown.ToHtml(source));

    [Fact]
    public void Bold_wins_over_italic_so_double_stars_are_not_eaten_as_two_emphases()
        => Assert.Equal("<p><strong>a</strong> and <em>b</em></p>", MiniMarkdown.ToHtml("**a** and *b*"));

    [Fact]
    public void A_blank_line_starts_a_new_paragraph()
        => Assert.Equal("<p>One.</p><p>Two.</p>", MiniMarkdown.ToHtml("One.\n\nTwo."));

    [Fact]
    public void A_single_newline_is_a_line_break_inside_the_paragraph()
        => Assert.Equal("<p>One.<br />Two.</p>", MiniMarkdown.ToHtml("One.\nTwo."));

    [Fact]
    public void Carriage_returns_do_not_produce_stray_breaks()
        => Assert.Equal("<p>One.</p><p>Two.</p>", MiniMarkdown.ToHtml("One.\r\n\r\nTwo."));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Nothing_in_means_nothing_out(string? source)
        => Assert.Equal("", MiniMarkdown.ToHtml(source));

    [Fact]
    public void An_unclosed_marker_is_left_as_literal_text()
    {
        var html = MiniMarkdown.ToHtml("**not closed and `neither is this");

        Assert.DoesNotContain("<strong>", html);
        Assert.DoesNotContain("<code>", html);
        Assert.Contains("**not closed", html);
    }

    [Fact]
    public void Markup_cannot_be_smuggled_through_a_code_span()
    {
        var html = MiniMarkdown.ToHtml("`<b>bold</b>`");

        Assert.Equal("<p><code>&lt;b&gt;bold&lt;/b&gt;</code></p>", html);
    }
}
