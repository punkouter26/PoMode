using Microsoft.Playwright;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class CanvasTests(AppFixture app)
{
    private static readonly LocatorAssertionsToBeVisibleOptions Visible =
        new() { Timeout = AppFixture.ExpectTimeoutMs };

    /// <summary>Uploads the silent fixture and waits for the canvas to exist. The fixture yields
    /// FakePitchTracker's 8 notes and (correctly, on silence) zero chords — so the note lane is
    /// populated and the chord lane is legitimately empty.</summary>
    private static async Task<ILocator> UploadAndGetCanvasAsync(IPage page, string baseUrl)
    {
        await page.GotoAsync(baseUrl);
        var wavPath = Path.Combine(Path.GetTempPath(), $"pomode-canvas-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, TestAudio.MakeWav(seconds: 0.5));
        try
        {
            await page.Locator("input[type=file]").SetInputFilesAsync(wavPath);
            await Assertions.Expect(page.GetByText("Analysis complete")).ToBeVisibleAsync(Visible);
        }
        finally
        {
            File.Delete(wavPath);
        }

        var canvas = page.Locator("canvas.analysis-canvas");
        await Assertions.Expect(canvas).ToBeVisibleAsync(Visible);
        // The module sets data-painted once its first frame has been drawn.
        await Assertions.Expect(canvas).ToHaveAttributeAsync("data-painted", "1",
            new() { Timeout = AppFixture.ExpectTimeoutMs });
        return canvas;
    }

    [Fact]
    public async Task The_canvas_renders_and_actually_paints_more_than_a_flat_background()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();

        var canvas = await UploadAndGetCanvasAsync(page, app.BaseUrl);

        var box = await canvas.BoundingBoxAsync();
        Assert.NotNull(box);
        Assert.True(box.Width > 100, $"canvas width was {box.Width}");
        Assert.True(box.Height > 100, $"canvas height was {box.Height}");

        // Count distinct pixel colours. A blank or single-fill canvas gives 1-2; lanes plus note
        // capsules plus the playhead must give more, which proves real drawing happened.
        var distinctColours = await canvas.EvaluateAsync<int>(@"canvas => {
            const ctx = canvas.getContext('2d');
            const data = ctx.getImageData(0, 0, canvas.width, canvas.height).data;
            const seen = new Set();
            for (let i = 0; i < data.length; i += 4) {
                seen.add((data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3]);
            }
            return seen.size;
        }");
        Assert.True(distinctColours > 3, $"only {distinctColours} distinct colours were painted");
    }

    [Fact]
    public async Task Wheel_zoom_narrows_the_visible_time_range()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();

        var canvas = await UploadAndGetCanvasAsync(page, app.BaseUrl);
        var before = await SpanAsync(canvas);

        // HoverAsync scrolls the canvas into view first; the page is taller than the viewport.
        await canvas.HoverAsync();
        await page.Mouse.WheelAsync(0, -600); // negative deltaY zooms in

        await Assertions.Expect(canvas).Not.ToHaveAttributeAsync("data-view-end", $"{before.End}",
            new() { Timeout = AppFixture.ExpectTimeoutMs });
        var after = await SpanAsync(canvas);
        Assert.True(after.Span < before.Span,
            $"span did not narrow: {before.Span} -> {after.Span}");
    }

    [Fact]
    public async Task Clicking_the_canvas_seeks_and_moves_the_playhead()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();

        var canvas = await UploadAndGetCanvasAsync(page, app.BaseUrl);
        Assert.Equal(0.0, await PlayheadAsync(canvas), precision: 3);

        // Locator.ClickAsync takes element-relative coordinates and scrolls into view for us; raw
        // Mouse.ClickAsync with a BoundingBox would miss, because the box is in viewport coordinates
        // and the lower part of the canvas sits below the fold on this page.
        await ClickCanvasAsync(canvas, 0.75f, 0.3f);

        await Assertions.Expect(canvas).Not.ToHaveAttributeAsync("data-playhead", "0",
            new() { Timeout = AppFixture.ExpectTimeoutMs });
        Assert.True(await PlayheadAsync(canvas) > 0, "playhead did not move");
    }

    /// <summary>Clicks a fraction across and down the canvas, in element-relative coordinates.</summary>
    internal static async Task ClickCanvasAsync(ILocator canvas, float acrossFraction, float downFraction)
    {
        var box = await canvas.BoundingBoxAsync();
        await canvas.ClickAsync(new LocatorClickOptions
        {
            Position = new Position { X = box!.Width * acrossFraction, Y = box.Height * downFraction },
        });
    }

    private static async Task<(double Start, double End, double Span)> SpanAsync(ILocator canvas)
    {
        var start = double.Parse(await canvas.GetAttributeAsync("data-view-start") ?? "0");
        var end = double.Parse(await canvas.GetAttributeAsync("data-view-end") ?? "0");
        return (start, end, end - start);
    }

    private static async Task<double> PlayheadAsync(ILocator canvas)
        => double.Parse(await canvas.GetAttributeAsync("data-playhead") ?? "0");
}
