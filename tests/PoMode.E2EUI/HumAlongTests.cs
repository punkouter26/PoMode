using Microsoft.Playwright;
using Xunit;

namespace PoMode.E2EUI;

/// <summary>
/// The Mode Lab's hum-along flow, driven in a real browser: roll a progression, record over it, hear
/// the take back, then send it to the analyzer. Everything here lives in JavaScript that no server
/// test can reach — the microphone, the count-in, and the shared AudioContext that
/// <c>hum-recorder.js</c> and <c>modal-player.js</c> must resolve to the same instance of, since the
/// take is trimmed to bar one by comparing their clocks.
///
/// <para>Assertions go through the <c>data-hum-*</c> attributes the recorder publishes, the same
/// contract mixer.js, canvas.js and modal-player.js keep — never the module's internals.</para>
/// </summary>
[Collection("App")]
public class HumAlongTests(AppFixture app)
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

    private async Task<IPage> ModeLabPageAsync(IBrowser browser)
    {
        var context = await browser.NewContextAsync();
        await context.GrantPermissionsAsync(["microphone"]);
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{app.BaseUrl}/modes");

        await Assertions.Expect(page.GetByText("Mode Lab").First).ToBeVisibleAsync(Visible);
        // The record button stays disabled until a melody (and therefore a chord loop) exists.
        await Assertions.Expect(page.Locator("#hum-record")).ToBeEnabledAsync(
            new LocatorAssertionsToBeEnabledOptions { Timeout = AppFixture.ExpectTimeoutMs });
        return page;
    }

    private static async Task<string?> StateAsync(IPage page, string attribute) =>
        await page.Locator("body").GetAttributeAsync(attribute);

    /// <summary>Records for roughly <paramref name="seconds"/> of loop time and stops.</summary>
    private static async Task RecordATakeAsync(IPage page, double seconds = 3.0)
    {
        await page.Locator("#hum-record").ClickAsync();

        // The count-in runs first, so "recording" is several beats away — waiting on the published
        // state rather than a sleep is what keeps this honest at any tempo.
        await Assertions.Expect(page.Locator("body[data-hum-recorder='recording']")).ToHaveCountAsync(1, Count);
        await Assertions.Expect(page.Locator("[data-hum-state='Recording']")).ToHaveCountAsync(1, Count);

        // Real elapsed time: the take must contain more than MIN_TAKE_SECONDS of audio after the
        // trim, or the recorder refuses to encode it.
        await page.WaitForTimeoutAsync((float)(seconds * 1000));
        await page.Locator("#hum-stop").ClickAsync();
    }

    [Fact]
    public async Task Rolling_new_chords_changes_the_progression()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await ModeLabPageAsync(browser);

        var before = await page.Locator("#prog-select").InnerTextAsync();
        await page.Locator("#roll-chords").ClickAsync();

        // The roll re-rolls rather than allowing a no-op, so the name must actually move.
        await Assertions.Expect(page.Locator("#prog-select")).Not.ToHaveTextAsync(
            before, new LocatorAssertionsToHaveTextOptions { Timeout = AppFixture.ExpectTimeoutMs });
    }

    [Fact]
    public async Task A_take_records_over_the_chords_and_is_offered_for_review()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(FakeMicLaunch);
        var page = await ModeLabPageAsync(browser);

        await RecordATakeAsync(page);

        // Captured, with audio actually in hand — a take that failed to encode leaves 'no' here and
        // sends the strip back to Idle instead.
        await Assertions.Expect(page.Locator("[data-hum-state='Captured']")).ToHaveCountAsync(1, Count);
        Assert.Equal("yes", await StateAsync(page, "data-hum-has-take"));
        await Assertions.Expect(page.Locator("#hum-analyze")).ToBeVisibleAsync(Visible);

        // The chords must have been looping underneath, and the melody must not have been: a flute
        // lead sounding into an open microphone is the one thing this mode exists to prevent.
        var backing = await StateAsync(page, "data-modal-backing-count");
        Assert.True(int.TryParse(backing, out var backingCount) && backingCount > 0,
            $"expected the chord backing to have been playing, got '{backing}'");
    }

    [Fact]
    public async Task A_captured_take_can_be_heard_back_against_its_chords()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(FakeMicLaunch);
        var page = await ModeLabPageAsync(browser);

        await RecordATakeAsync(page);
        await Assertions.Expect(page.Locator("[data-hum-state='Captured']")).ToHaveCountAsync(1, Count);

        await page.Locator("#hum-review").ClickAsync();

        // Reviewing means the recording and the loop are both sounding, scheduled off one instant.
        await Assertions.Expect(page.Locator("body[data-hum-recorder='reviewing']")).ToHaveCountAsync(1, Count);
        await Assertions.Expect(page.Locator("body[data-modal-player='playing']")).ToHaveCountAsync(1, Count);

        await page.Locator("#hum-review").ClickAsync();
        await Assertions.Expect(page.Locator("body[data-hum-recorder='idle']")).ToHaveCountAsync(1, Count);
        // Stopping the review must stop the chords too, not leave them looping over nothing.
        await Assertions.Expect(page.Locator("body[data-modal-player='stopped']")).ToHaveCountAsync(1, Count);
    }

    [Fact]
    public async Task Redoing_a_take_throws_it_away_and_returns_to_the_record_button()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(FakeMicLaunch);
        var page = await ModeLabPageAsync(browser);

        await RecordATakeAsync(page);
        await Assertions.Expect(page.Locator("[data-hum-state='Captured']")).ToHaveCountAsync(1, Count);

        await page.Locator("#hum-redo").ClickAsync();

        await Assertions.Expect(page.Locator("[data-hum-state='Idle']")).ToHaveCountAsync(1, Count);
        await Assertions.Expect(page.Locator("#hum-record")).ToBeVisibleAsync(Visible);
        // The bytes and the decoded buffer are dropped in the JS module too — a discarded take must
        // not stay alive for the rest of the session.
        await Assertions.Expect(page.Locator("body[data-hum-has-take='no']")).ToHaveCountAsync(1, Count);
    }

    [Fact]
    public async Task Saving_a_take_sends_it_to_the_analyzer_with_the_progression_it_was_sung_over()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(FakeMicLaunch);
        var page = await ModeLabPageAsync(browser);

        await RecordATakeAsync(page);
        await Assertions.Expect(page.Locator("[data-hum-state='Captured']")).ToHaveCountAsync(1, Count);

        await page.Locator("#hum-analyze").ClickAsync();

        // The analyzer page, on this job — the whole point of the feature.
        await page.WaitForURLAsync(
            url => url.Contains("job="), new PageWaitForURLOptions { Timeout = AppFixture.ExpectTimeoutMs });

        // And it says what it is. Without this the page reports a mode on an unexplained file.
        await Assertions.Expect(page.Locator("[data-take-origin='HumTake']")).ToHaveCountAsync(1, Count);
        await Assertions.Expect(page.GetByText("Hummed over").First).ToBeVisibleAsync(Visible);
    }
}
