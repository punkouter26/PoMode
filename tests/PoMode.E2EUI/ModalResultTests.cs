using Microsoft.Playwright;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class ModalResultTests(AppFixture app)
{
    [Fact]
    public async Task Results_show_key_mode_and_a_midi_link()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        var wavPath = Path.Combine(Path.GetTempPath(), $"pomode-modal-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, TestAudio.MakeWav(seconds: 0.5));
        try
        {
            var visible = new LocatorAssertionsToBeVisibleOptions { Timeout = AppFixture.ExpectTimeoutMs };
            await page.Locator("input[type=file]").SetInputFilesAsync(wavPath);

            await Assertions.Expect(page.GetByText("Analysis complete")).ToBeVisibleAsync(visible);
            await Assertions.Expect(page.GetByText("Key:")).ToBeVisibleAsync(visible);
            await Assertions.Expect(page.GetByText("(estimated)")).ToBeVisibleAsync(visible);
            await Assertions.Expect(page.GetByText("Download MIDI")).ToBeVisibleAsync(visible);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }
}
