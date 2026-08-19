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
            var hidden = new LocatorAssertionsToBeHiddenOptions { Timeout = AppFixture.ExpectTimeoutMs };
            // Cold load: no data on screen yet, mock or otherwise, so the banner is hidden.
            await Assertions.Expect(page.GetByText("USING MOCK DATA")).ToBeHiddenAsync(hidden);

            await page.Locator("input[type=file]").SetInputFilesAsync(wavPath);

            await Assertions.Expect(page.GetByText("Analysis complete")).ToBeVisibleAsync(visible);
            // Real free executors won PitchTracking (YinPitchTracker) and ChordDetecting
            // (ChromaChordRecognizer) in this no-model test host, and the uploaded fixture is
            // silence — so zero notes and zero chords are the true output, not a regression.
            // (Before the classic-DSP fallbacks existed, FakePitchTracker's canned 8 notes showed here.)
            await Assertions.Expect(page.GetByText("0 notes · 0 chords")).ToBeVisibleAsync(visible);
            // Separating still lands on FakeStemSeparator here, because AppFixture deliberately
            // sets Models:AutoDownload=false and isolates Models:RootPath so these browser tests
            // stay fast, deterministic, and network-free (see AppFixture's comment). With one
            // stage still fake, the plan touches a fake executor and SetMock() flips the banner
            // back on once that completed job lands — see MockDataState.PlanContainsFakeExecutor
            // and the SetMock call in Home.razor. The banner only stays hidden once the Onnx stem
            // separator is also available (a real model download, exercised elsewhere, not by
            // this fast in-memory-model browser suite).
            await Assertions.Expect(page.GetByText("USING MOCK DATA")).ToBeVisibleAsync(visible);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }
}
