using Microsoft.Playwright;
using Xunit;

namespace PoMode.E2EUI;

/// <summary>
/// The Practice page's sing-over-chords flow, driven in a real browser: record over the loop, hear
/// the take back, then send it to the analyzer. Everything here lives in JavaScript that no server
/// test can reach — the microphone, the count-in, and the shared AudioContext that
/// <c>hum-recorder.js</c> and <c>modal-player.js</c> must resolve to the same instance of, since the
/// take is trimmed to bar one by comparing their clocks.
///
/// <para>This used to drive the Mode Lab, which carried a second copy of the same flow behind
/// different element ids. There is one copy now, on the page whose whole subject it is.</para>
///
/// <para>Assertions go through the <c>data-hum-*</c> attributes the recorder publishes, the same
/// contract mixer.js, canvas.js and modal-player.js keep — never the module's internals.</para>
/// </summary>
[Collection("App")]
public class SingOverChordsTests(AppFixture app)
{
    private static readonly LocatorAssertionsToBeVisibleOptions Visible =
        new() { Timeout = AppFixture.ExpectTimeoutMs };

    private static LocatorAssertionsToHaveCountOptions Count =>
        new() { Timeout = AppFixture.ExpectTimeoutMs };

    /// <summary>
    /// Chromium's fake capture device: a synthesised tone on a real MediaStream, so the recorder
    /// takes the ordinary getUserMedia path and produces genuine audio rather than silence. The fake
    /// UI flag auto-accepts the permission prompt, which is modal and would otherwise hang the run.
    /// </summary>
    private static BrowserTypeLaunchOptions FakeMicLaunch => new()
    {
        Args =
        [
            "--use-fake-device-for-media-stream",
            "--use-fake-ui-for-media-stream",
            "--autoplay-policy=no-user-gesture-required",
        ],
    };

    private async Task<IPage> PracticePageAsync(IBrowser browser)
    {
        var context = await browser.NewContextAsync();
        await context.GrantPermissionsAsync(["microphone"]);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/practice");

        // The sing button appears only once a progression (and therefore a chord loop) exists.
        await Assertions.Expect(page.Locator("#practice-sing")).ToBeVisibleAsync(Visible);
        return page;
    }

    private static async Task<string?> StateAsync(IPage page, string attribute) =>
        await page.Locator("body").GetAttributeAsync(attribute);

    /// <summary>Records for roughly <paramref name="seconds"/> of loop time and stops.</summary>
    private static async Task RecordATakeAsync(IPage page, double seconds = 3.0)
    {
        await page.Locator("#practice-sing").ClickAsync();

        // The count-in runs first, so "recording" is several beats away — waiting on the published
        // state rather than a sleep is what keeps this honest at any tempo.
        await Assertions.Expect(page.Locator("body[data-hum-recorder='recording']")).ToHaveCountAsync(1, Count);
        await Assertions.Expect(page.Locator("[data-practice-state='Recording']")).ToHaveCountAsync(1, Count);

        // Real elapsed time: the take must contain more than MIN_TAKE_SECONDS of audio after the
        // trim, or the recorder refuses to encode it.
        await page.WaitForTimeoutAsync((float)(seconds * 1000));
        await page.Locator("#practice-stop").ClickAsync();
    }

    [Fact]
    public async Task A_take_records_over_the_chords_and_is_offered_for_review()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(FakeMicLaunch);
        var page = await PracticePageAsync(browser);

        await RecordATakeAsync(page);

        // Captured, with audio actually in hand — a take that failed to encode leaves 'no' here and
        // sends the page back to Idle instead.
        await Assertions.Expect(page.Locator("[data-practice-state='Captured']")).ToHaveCountAsync(1, Count);
        Assert.Equal("yes", await StateAsync(page, "data-hum-has-take"));
        await Assertions.Expect(page.Locator("#practice-analyze")).ToBeVisibleAsync(Visible);

        // The chords must have been looping underneath: singing into an open microphone with no
        // harmony is the one thing this page exists to prevent.
        var backing = await StateAsync(page, "data-modal-backing-count");
        Assert.True(int.TryParse(backing, out var backingCount) && backingCount > 0,
            $"expected the chord backing to have been playing, got '{backing}'");
    }

    [Fact]
    public async Task A_captured_take_can_be_heard_back_against_its_chords()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(FakeMicLaunch);
        var page = await PracticePageAsync(browser);

        await RecordATakeAsync(page);
        await Assertions.Expect(page.Locator("[data-practice-state='Captured']")).ToHaveCountAsync(1, Count);

        await page.Locator("#practice-review").ClickAsync();

        // Reviewing means the recording and the loop are both sounding, scheduled off one instant.
        await Assertions.Expect(page.Locator("body[data-hum-recorder='reviewing']")).ToHaveCountAsync(1, Count);
        await Assertions.Expect(page.Locator("body[data-modal-player='playing']")).ToHaveCountAsync(1, Count);

        await page.Locator("#practice-review").ClickAsync();
        await Assertions.Expect(page.Locator("body[data-hum-recorder='idle']")).ToHaveCountAsync(1, Count);
        // Stopping the review must stop the chords too, not leave them looping over nothing.
        await Assertions.Expect(page.Locator("body[data-modal-player='stopped']")).ToHaveCountAsync(1, Count);
    }

    [Fact]
    public async Task Saving_a_take_sends_it_to_the_analyzer_with_the_progression_it_was_sung_over()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(FakeMicLaunch);
        var page = await PracticePageAsync(browser);

        await RecordATakeAsync(page);
        await Assertions.Expect(page.Locator("[data-practice-state='Captured']")).ToHaveCountAsync(1, Count);

        await page.Locator("#practice-analyze").ClickAsync();

        // The analyzer page, on this job — the whole point of the feature.
        await page.WaitForURLAsync(
            url => url.Contains("job="), new PageWaitForURLOptions { Timeout = AppFixture.ExpectTimeoutMs });

        // And it says what it is. Without this the page reports a mode on an unexplained file.
        await Assertions.Expect(page.Locator("[data-take-origin='HumTake']")).ToHaveCountAsync(1, Count);
        await Assertions.Expect(page.GetByText("Hummed over").First).ToBeVisibleAsync(Visible);
    }
}
