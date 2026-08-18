using Microsoft.Playwright;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class ShellSmokeTests(AppFixture app)
{
    [Fact]
    public async Task Shell_renders_header_without_mock_banner_on_cold_load()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();

        await page.GotoAsync(app.BaseUrl);

        // First WASM load downloads the framework and can be slow; raise only the assertion
        // timeout (not a Task.Delay) so Playwright's auto-wait tolerates it.
        var expectOptions = new LocatorAssertionsToBeVisibleOptions { Timeout = AppFixture.ExpectTimeoutMs };
        // No job has run yet — there's nothing on screen, mock or otherwise, so the banner must stay
        // hidden. It only flips on when a completed job's plan actually touched a fake/placeholder
        // executor (see MockDataState.SetMock in Home.razor).
        var hiddenOptions = new LocatorAssertionsToBeHiddenOptions { Timeout = AppFixture.ExpectTimeoutMs };
        await Assertions.Expect(page.GetByText("USING MOCK DATA")).ToBeHiddenAsync(hiddenOptions);
        await Assertions.Expect(page.GetByText("PoMode").First).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "Diagnostics" })).ToBeVisibleAsync(expectOptions);
    }
}
