using Microsoft.Playwright;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class ModalHudTests(AppFixture app)
{
    private static readonly LocatorAssertionsToBeVisibleOptions Visible =
        new() { Timeout = AppFixture.ExpectTimeoutMs };

    /// <summary>A single WAV holding four two-second triads, so the pipeline produces real chords and
    /// therefore real modal windows to click. The silent fixture used elsewhere yields zero chords.</summary>
    private static byte[] Progression(params (int Root, string Quality, double Seconds)[] chords)
    {
        using var stream = new MemoryStream();
        foreach (var (root, quality, seconds) in chords)
        {
            var wav = TestAudio.MakeChord(seconds, TestAudio.Triad(root, quality));
            var skipHeader = stream.Length == 0 ? 0 : 44;
            stream.Write(wav, skipHeader, wav.Length - skipHeader);
        }
        var bytes = stream.ToArray();
        var dataSize = bytes.Length - 44;
        BitConverter.GetBytes(36 + dataSize).CopyTo(bytes, 4);
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);
        return bytes;
    }

    private static async Task<IPage> AnalysedPageAsync(IBrowser browser, string baseUrl, byte[] audio)
    {
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await page.GotoAsync(baseUrl);

        var wavPath = Path.Combine(Path.GetTempPath(), $"pomode-hud-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, audio);
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
    public async Task The_hud_reports_the_primary_mode_and_tonic()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await AnalysedPageAsync(browser, app.BaseUrl, Progression(
            (0, "maj", 2.0), (9, "min", 2.0), (5, "maj", 2.0), (7, "maj", 2.0)));

        await Assertions.Expect(page.GetByText("Primary mode")).ToBeVisibleAsync(Visible);
        // Tonic C with a C-Am-F-G progression; the engine names Ionian from the sung material.
        await Assertions.Expect(page.Locator(".hud-headline").First).ToContainTextAsync("C");
        await Assertions.Expect(page.Locator(".hud-headline").First).ToContainTextAsync("Ionian");
        await Assertions.Expect(page.GetByText("confidence")).ToBeVisibleAsync(Visible);
    }

    [Fact]
    public async Task Before_any_click_the_window_cards_invite_a_selection_rather_than_inventing_one()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await AnalysedPageAsync(browser, app.BaseUrl, Progression(
            (0, "maj", 2.0), (9, "min", 2.0), (5, "maj", 2.0), (7, "maj", 2.0)));

        await Assertions.Expect(page.GetByText("Click a chord block on the timeline to inspect a window."))
            .ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.Locator(".degree-grid")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Clicking_the_timeline_fills_in_the_chord_mask_and_degree_grid()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await AnalysedPageAsync(browser, app.BaseUrl, Progression(
            (0, "maj", 2.0), (9, "min", 2.0), (5, "maj", 2.0), (7, "maj", 2.0)));

        // 12.5% across the ~8 s track lands inside the first chord.
        await CanvasTests.ClickCanvasAsync(page.Locator("canvas.analysis-canvas"), 0.125f, 0.85f);

        await Assertions.Expect(page.Locator(".hud-chord")).ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.Locator(".hud-mask")).ToContainTextAsync("0x");
        await Assertions.Expect(page.Locator(".degree-grid .degree")).ToHaveCountAsync(12);
    }

    [Fact]
    public async Task A_window_without_enough_sung_notes_says_so_instead_of_showing_a_fake_match()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await AnalysedPageAsync(browser, app.BaseUrl, Progression(
            (0, "maj", 2.0), (9, "min", 2.0), (5, "maj", 2.0), (7, "maj", 2.0)));

        // FakePitchTracker's eight notes all land near the start of the track, so the later windows
        // genuinely have fewer than the three distinct sung pitch classes spec §6 requires.
        await CanvasTests.ClickCanvasAsync(page.Locator("canvas.analysis-canvas"), 0.9f, 0.85f);

        await Assertions.Expect(page.Locator(".hud-chord")).ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.GetByText("Not enough sung notes in this window to name a mode."))
            .ToBeVisibleAsync(Visible);
    }

    /// <summary>
    /// Was ModalResultTests, which asserted the deleted ModalResultView's "Key:" heading. The silent
    /// fixture yields zero chords, so there are no modal windows and no primary mode — the HUD must
    /// say "mode unclear" rather than invent one, and the MIDI link must still be offered.
    /// </summary>
    [Fact]
    public async Task A_track_with_no_detected_chords_still_renders_an_honest_hud()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await AnalysedPageAsync(browser, app.BaseUrl, TestAudio.MakeWav(seconds: 0.5));

        await Assertions.Expect(page.GetByText("Primary mode")).ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.Locator(".hud-headline").First).ToContainTextAsync("mode unclear");
        await Assertions.Expect(page.GetByText("No window had enough sung material to name a mode."))
            .ToBeVisibleAsync(Visible);
        await Assertions.Expect(page.GetByText("(estimated)")).ToBeVisibleAsync(Visible);
        await page.Locator(".export-toggle").ClickAsync();
        await Assertions.Expect(page.GetByText("Download MIDI")).ToBeVisibleAsync(Visible);
    }

    [Fact]
    public async Task Each_planned_stage_carries_a_tier_badge_with_an_explanatory_title()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await AnalysedPageAsync(browser, app.BaseUrl, Progression((0, "maj", 2.0)));

        var badges = page.Locator(".tier-badge");
        await Assertions.Expect(badges).ToHaveCountAsync(4); // one per pipeline stage
        // Every stage runs locally in this fixture (no models downloaded, no cloud keys).
        await Assertions.Expect(badges.First).ToHaveAttributeAsync("title", "Ran locally on this machine");
    }
}
