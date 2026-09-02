using System.Globalization;
using Microsoft.Playwright;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

[Collection("App")]
public class TuningCorrectionTests(AppFixture app)
{
    /// <summary>How far sharp of A=440 the synthesised singer is. Slightly off, not another note.</summary>
    private const double SharpCents = 6.0;

    /// <summary>C4 up to C5, the scale the recording sings.</summary>
    private static readonly int[] CMajorScale = [60, 62, 64, 65, 67, 69, 71, 72];

    /// <summary>
    /// One WAV of the scale with every note detuned by the same amount, which is what a singer who
    /// is a few cents sharp throughout actually produces. Segments are concatenated past their
    /// 44-byte headers and the RIFF sizes patched, the same way ModalHudTests builds a progression.
    /// </summary>
    private static byte[] DetunedScale(double cents)
    {
        using var stream = new MemoryStream();
        foreach (var midi in CMajorScale)
        {
            var frequency = 440.0
                * Math.Pow(2.0, (midi - 69) / 12.0)
                * Math.Pow(2.0, cents / 1200.0);
            var tone = TestAudio.MakeTone(seconds: 0.8, frequencyHz: frequency);
            var skipHeader = stream.Length == 0 ? 0 : 44;
            stream.Write(tone, skipHeader, tone.Length - skipHeader);
        }

        var bytes = stream.ToArray();
        var dataSize = bytes.Length - 44;
        BitConverter.GetBytes(36 + dataSize).CopyTo(bytes, 4);
        BitConverter.GetBytes(dataSize).CopyTo(bytes, 40);
        return bytes;
    }

    /// <summary>
    /// The end-to-end claim: a recording that arrives a few cents sharp is measured, the Analyze
    /// page says so and by how much, and the opening note is still read as a true C rather than
    /// drifting toward the neighbouring semitone.
    /// </summary>
    [Fact]
    public async Task A_slightly_sharp_recording_is_measured_and_its_first_note_still_reads_as_C()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        var wavPath = Path.Combine(Path.GetTempPath(), $"pomode-sharp-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(wavPath, DetunedScale(SharpCents));
        try
        {
            var visible = new LocatorAssertionsToBeVisibleOptions { Timeout = AppFixture.ExpectTimeoutMs };
            await page.Locator("input[type=file]").SetInputFilesAsync(wavPath);

            var notice = page.Locator(".tuning-notice");
            await Assertions.Expect(notice).ToBeVisibleAsync(visible);

            var reported = double.Parse(
                await notice.GetAttributeAsync("data-tuning-cents") ?? "0",
                CultureInfo.InvariantCulture);
            Assert.InRange(reported, SharpCents - 4.0, SharpCents + 4.0);
            await Assertions.Expect(notice).ToContainTextAsync("sharp");

            // The opening note, named server-side and mirrored onto the canvas. Sharp of C is still
            // C: the point is that measuring the offset does not push the note off its own name.
            var canvas = page.Locator("canvas.analysis-canvas");
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-painted", "1",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = AppFixture.ExpectTimeoutMs });
            await Assertions.Expect(canvas).ToHaveAttributeAsync("data-first-note", "C4",
                new LocatorAssertionsToHaveAttributeOptions { Timeout = AppFixture.ExpectTimeoutMs });
        }
        finally
        {
            File.Delete(wavPath);
        }
    }
}
