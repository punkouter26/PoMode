using Microsoft.Playwright;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class ShellSmokeTests(AppFixture app)
{
    [Fact]
    public async Task Shell_renders_header_and_mock_data_banner()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();

        await page.GotoAsync(app.BaseUrl);

        // First WASM load downloads the framework and can be slow; raise only the assertion
        // timeout (not a Task.Delay) so Playwright's auto-wait tolerates it.
        var expectOptions = new LocatorAssertionsToBeVisibleOptions { Timeout = 30000f };
        await Assertions.Expect(page.GetByText("USING MOCK DATA")).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.GetByText("PoMode").First).ToBeVisibleAsync(expectOptions);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Export MIDI" })).ToBeVisibleAsync(expectOptions);
    }
}
