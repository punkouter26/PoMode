using Microsoft.Playwright;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

/// <summary>
/// AppFixture points the copilot at a dead loopback port, so these tests pin down the unavailable
/// path — the one a user without Ollama actually sees.
/// </summary>
[Collection("App")]
public class CopilotCardTests(AppFixture app)
{
    private static readonly LocatorAssertionsToBeVisibleOptions Visible =
        new() { Timeout = AppFixture.ExpectTimeoutMs };

    private static byte[] Progression()
    {
        using var stream = new MemoryStream();
        foreach (var (root, quality) in new[] { (0, "maj"), (9, "min"), (5, "maj"), (7, "maj") })
        {
            var wav = TestAudio.MakeChord(2.0, TestAudio.Triad(root, quality));
            var skipHeader = stream.Length == 0 ? 0 : 44;
            stream.Write(wav, skipHeader, wav.Length - skipHeader);
        }
        var bytes = stream.ToArray();
        var dataSize = bytes.Length - 44;
        BitConverter.GetBytes(36 + dataSize).CopyTo(bytes, 4);
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);
        return bytes;
    }

    private static async Task<IPage> AnalysedPageAsync(IBrowser browser, string baseUrl)
    {
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await page.GotoAsync(baseUrl);

        var wavPath = Path.Combine(Path.GetTempPath(), $"pomode-copilot-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, Progression());
        try
        {
            await page.Locator("input[type=file]").SetInputFilesAsync(wavPath);
            await Assertions.Expect(page.GetByText("Analysis complete")).ToBeVisibleAsync(Visible);
            await Assertions.Expect(page.Locator("canvas.analysis-canvas"))
                .ToHaveAttributeAsync("data-painted", "1", new() { Timeout = AppFixture.ExpectTimeoutMs });
        }
        finally
        {
            File.Delete(wavPath);
        }
        return page;
    }

    [Fact]
    public async Task The_card_waits_for_a_selection_before_offering_to_explain_anything()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await AnalysedPageAsync(browser, app.BaseUrl);

        await Assertions.Expect(page.GetByText("Copilot")).ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.GetByText("Select a window to have it explained.")).ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Explain" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task With_no_ollama_the_card_says_unavailable_and_never_shows_an_explanation()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await AnalysedPageAsync(browser, app.BaseUrl);

        await CanvasTests.ClickCanvasAsync(page.Locator("canvas.analysis-canvas"), 0.125f, 0.85f);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Explain" })).ToBeVisibleAsync(Visible);

        await page.GetByRole(AriaRole.Button, new() { Name = "Explain" }).ClickAsync();

        await Assertions.Expect(page.GetByText("Copilot unavailable")).ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.Locator(".copilot-body")).ToHaveCountAsync(0);
        // The reason must be shown, not swallowed.
        await Assertions.Expect(page.Locator(".copilot")).ToContainTextAsync("Ollama");
    }

    [Fact]
    public async Task After_a_failed_attempt_the_button_offers_a_retry()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await AnalysedPageAsync(browser, app.BaseUrl);

        await CanvasTests.ClickCanvasAsync(page.Locator("canvas.analysis-canvas"), 0.125f, 0.85f);
        await page.GetByRole(AriaRole.Button, new() { Name = "Explain" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Copilot unavailable")).ToBeVisibleAsync(Visible);

        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Regenerate" }))
            .ToBeVisibleAsync(Visible);
    }
}
