using Microsoft.Playwright;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class StemMixerTests(AppFixture app)
{
    private static readonly LocatorAssertionsToBeVisibleOptions Visible =
        new() { Timeout = AppFixture.ExpectTimeoutMs };

    /// <summary>Eight seconds of audio, so playback has room to advance measurably.</summary>
    private static byte[] EightSeconds() => TestAudio.MakeChord(8.0, TestAudio.Triad(0, "maj"));

    private static async Task<IPage> ReadyMixerPageAsync(IBrowser browser, string baseUrl)
    {
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        // Explicit view: these tests key off JobProgress's "Analysis complete", which Basic hides
        // once the job finishes, and the app now opens in Basic.
        await page.GotoAsync($"{baseUrl}/?view=advanced");

        var wavPath = Path.Combine(Path.GetTempPath(), $"pomode-mixer-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, EightSeconds());
        try
        {
            await page.Locator("input[type=file]").SetInputFilesAsync(wavPath);
            await Assertions.Expect(page.GetByText("Analysis complete")).ToBeVisibleAsync(Visible);
            // The module reports 'ready' once all three stems are fetched and decoded.
            await Assertions.Expect(page.Locator(".mixer"))
                .ToHaveAttributeAsync("data-mixer-status", "ready",
                    new() { Timeout = AppFixture.ExpectTimeoutMs });
        }
        finally
        {
            File.Delete(wavPath);
        }
        return page;
    }

    private static async Task<double> MixerTimeAsync(IPage page)
        => double.Parse(await page.Locator(".mixer").GetAttributeAsync("data-mixer-time") ?? "0");

    [Fact]
    public async Task The_transport_and_all_three_stem_modes_are_offered()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await ReadyMixerPageAsync(browser, app.BaseUrl);

        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Play" })).ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Full Mix" })).ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Solo Vocals" })).ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Solo Backing" })).ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.Locator(".mixer"))
            .ToHaveAttributeAsync("data-mixer-duration", "8.000");
    }

    [Fact]
    public async Task Play_advances_the_clock_and_the_canvas_playhead_follows_it()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await ReadyMixerPageAsync(browser, app.BaseUrl);

        Assert.Equal(0.0, await MixerTimeAsync(page), precision: 2);
        await page.GetByRole(AriaRole.Button, new() { Name = "Play" }).ClickAsync();

        await Assertions.Expect(page.Locator(".mixer"))
            .ToHaveAttributeAsync("data-mixer-status", "playing", new() { Timeout = AppFixture.ExpectTimeoutMs });
        // The mixer owns the clock and pushes it into the canvas, so the playhead must move without
        // any Blazor round trip.
        await Assertions.Expect(page.Locator("canvas.analysis-canvas"))
            .Not.ToHaveAttributeAsync("data-playhead", "0.000", new() { Timeout = AppFixture.ExpectTimeoutMs });
        Assert.True(await MixerTimeAsync(page) > 0, "mixer clock did not advance");
    }

    [Fact]
    public async Task Pause_stops_the_clock_where_it_was()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await ReadyMixerPageAsync(browser, app.BaseUrl);

        await page.GetByRole(AriaRole.Button, new() { Name = "Play" }).ClickAsync();
        await Assertions.Expect(page.Locator(".mixer"))
            .Not.ToHaveAttributeAsync("data-mixer-time", "0.000", new() { Timeout = AppFixture.ExpectTimeoutMs });

        await page.GetByRole(AriaRole.Button, new() { Name = "Pause" }).ClickAsync();
        await Assertions.Expect(page.Locator(".mixer"))
            .ToHaveAttributeAsync("data-mixer-status", "paused", new() { Timeout = AppFixture.ExpectTimeoutMs });

        var atPause = await MixerTimeAsync(page);
        Assert.True(atPause > 0, "paused at zero");
        Assert.Equal(atPause, await MixerTimeAsync(page), precision: 3); // the clock is genuinely stopped
    }

    [Fact]
    public async Task Switching_to_solo_vocals_preserves_the_playback_position()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await ReadyMixerPageAsync(browser, app.BaseUrl);

        await page.GetByRole(AriaRole.Button, new() { Name = "Play" }).ClickAsync();
        await Assertions.Expect(page.Locator(".mixer"))
            .Not.ToHaveAttributeAsync("data-mixer-time", "0.000", new() { Timeout = AppFixture.ExpectTimeoutMs });
        var before = await MixerTimeAsync(page);

        await page.GetByRole(AriaRole.Button, new() { Name = "Solo Vocals" }).ClickAsync();

        await Assertions.Expect(page.Locator(".mixer"))
            .ToHaveAttributeAsync("data-mixer-mode", "vocals", new() { Timeout = AppFixture.ExpectTimeoutMs });
        // A gain ramp must not restart the sources, so the clock keeps running from where it was.
        var after = await MixerTimeAsync(page);
        Assert.True(after >= before, $"position went backwards: {before} -> {after}");
        await Assertions.Expect(page.Locator(".mixer"))
            .ToHaveAttributeAsync("data-mixer-status", "playing");
    }

    [Fact]
    public async Task Clicking_the_canvas_seeks_the_audio_too()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await ReadyMixerPageAsync(browser, app.BaseUrl);

        await CanvasTests.ClickCanvasAsync(page.Locator("canvas.analysis-canvas"), 0.6f, 0.3f);

        await Assertions.Expect(page.Locator(".mixer"))
            .Not.ToHaveAttributeAsync("data-mixer-time", "0.000", new() { Timeout = AppFixture.ExpectTimeoutMs });
        var seeked = await MixerTimeAsync(page);
        Assert.InRange(seeked, 4.0, 5.6); // 60% of an 8 s track
    }
}
