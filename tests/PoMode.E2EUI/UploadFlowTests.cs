using Microsoft.Playwright;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class UploadFlowTests(AppFixture app)
{
    [Fact]
    public async Task Upload_runs_pipeline_shows_results_and_keeps_mock_banner_while_any_stage_is_fake()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        var wavPath = Path.Combine(Path.GetTempPath(), $"pomode-upload-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, TestAudio.MakeWav(seconds: 0.5));
        try
        {
            var visible = new LocatorAssertionsToBeVisibleOptions { Timeout = AppFixture.ExpectTimeoutMs };
            await Assertions.Expect(page.GetByText("USING MOCK DATA")).ToBeVisibleAsync(visible);

            await page.Locator("input[type=file]").SetInputFilesAsync(wavPath);

            await Assertions.Expect(page.GetByText("Analysis complete")).ToBeVisibleAsync(visible);
            await Assertions.Expect(page.GetByText("8 notes · 4 chords")).ToBeVisibleAsync(visible);
            // FakeChordRecognizer is still the only chord recognizer (Phase 5 adds a real one), so any
            // completed job's plan still touches a fake executor and the banner must stay on — see
            // MockDataState.PlanContainsFakeExecutor.
            await Assertions.Expect(page.GetByText("USING MOCK DATA")).ToBeVisibleAsync(visible);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }
}
