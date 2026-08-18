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
            // Phase 5: ChordDetecting now runs the real ChromaChordRecognizer (pure DSP, unconditionally
            // available, so it always outranks FakeChordRecognizer). The uploaded fixture is silence, and
            // real chord recognition on silence correctly finds zero chords rather than the fake's fixed
            // four — this is the true output, not a regression.
            await Assertions.Expect(page.GetByText("8 notes · 0 chords")).ToBeVisibleAsync(visible);
            // Separating and PitchTracking still land on FakeStemSeparator/FakePitchTracker here — not
            // because a real chord recognizer is missing (it isn't, see above), but because AppFixture
            // deliberately sets Models:AutoDownload=false and isolates Models:RootPath so these browser
            // tests stay fast, deterministic, and network-free (see AppFixture's comment). With two of
            // four stages still fake, the plan still touches a fake executor and SetMock() flips the
            // banner back on once that completed job lands — see MockDataState.PlanContainsFakeExecutor
            // and the SetMock call in Home.razor. The banner only stays hidden once the Onnx stem
            // separator and pitch tracker are also available (a real model download, exercised
            // elsewhere, not by this fast in-memory-model browser suite).
            await Assertions.Expect(page.GetByText("USING MOCK DATA")).ToBeVisibleAsync(visible);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }
}
