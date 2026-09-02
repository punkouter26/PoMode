using Microsoft.Playwright;
using Xunit;

namespace PoMode.E2EUI;

/// <summary>
/// Guards the Mode Lab's per-mode Sample buttons. The regression these cover: picking a second mode
/// while a first one was already sounding used to fall through to a melody-only swap, so the piano
/// kept the previous mode's chords, the loop kept the previous mode's length, and no restart ever
/// happened. The <c>data-modal-*</c> attributes published by modal-player.js are the assertion
/// surface, the same contract mixer.js and canvas.js keep.
/// </summary>
[Collection("App")]
public class ModeLabTests(AppFixture app)
{
    private static readonly LocatorAssertionsToBeVisibleOptions Visible =
        new() { Timeout = AppFixture.ExpectTimeoutMs };

    private static ILocator SampleButtonFor(IPage page, string modeName) =>
        page.Locator(".compact-mode-card", new() { HasText = modeName })
            .Locator(".compact-sample-btn");

    private async Task<IPage> ModeLabPageAsync(IBrowser browser)
    {
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/modes");
        // The first WASM load pulls the framework down; wait on the melody being generated rather
        // than on a timer, since the Sample buttons stay inert until a melody exists.
        await Assertions.Expect(page.GetByText("Mode Lab").First).ToBeVisibleAsync(Visible);
        await Assertions.Expect(SampleButtonFor(page, "Dorian").First).ToBeEnabledAsync(
            new LocatorAssertionsToBeEnabledOptions { Timeout = AppFixture.ExpectTimeoutMs });
        return page;
    }

    private static async Task<string?> StateAsync(IPage page, string attribute) =>
        await page.Locator("body").GetAttributeAsync(attribute);

    [Fact]
    public async Task Sampling_a_mode_starts_the_player()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await ModeLabPageAsync(browser);

        await SampleButtonFor(page, "Dorian").First.ClickAsync();

        await Assertions.Expect(page.Locator("body[data-modal-player='playing']"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = AppFixture.ExpectTimeoutMs });
        Assert.Equal("1", await StateAsync(page, "data-modal-plays"));
    }

    [Fact]
    public async Task Sampling_a_second_mode_while_playing_restarts_the_whole_arrangement()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await ModeLabPageAsync(browser);

        await SampleButtonFor(page, "Dorian").First.ClickAsync();
        await Assertions.Expect(page.Locator("body[data-modal-player='playing']"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = AppFixture.ExpectTimeoutMs });

        // The bug: this second click landed while the player was already running.
        await SampleButtonFor(page, "Lydian").First.ClickAsync();

        // A real restart bumps the play counter. The old code left it at 1 and only swapped the
        // lead line, which is exactly what made the button feel dead.
        await Assertions.Expect(page.Locator("body[data-modal-plays='2']"))
            .ToHaveCountAsync(1, new LocatorAssertionsToHaveCountOptions { Timeout = AppFixture.ExpectTimeoutMs });
        Assert.Equal("playing", await StateAsync(page, "data-modal-player"));

        // The piano must have been handed an arrangement, not left on the previous mode's chords.
        var backing = await StateAsync(page, "data-modal-backing-count");
        Assert.True(int.TryParse(backing, out var backingCount) && backingCount > 0,
            $"expected chord backing notes to be loaded, got '{backing}'");

        // And the newly picked card is the one reporting itself as playing.
        await Assertions.Expect(
            page.Locator(".compact-mode-card", new() { HasText = "Lydian" }).Locator(".compact-sample-btn.playing"))
            .ToBeVisibleAsync(Visible);
    }
}
