using System.Text.Json;
using Microsoft.Playwright;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.E2EUI;

/// <summary>
/// The JS side of spec §5's "specified once, implemented twice" contract: loads the served
/// <c>basic-pitch-decoder.js</c> module in a real browser, runs it on the same fixture the C#
/// decoder is tested against, and asserts note-for-note agreement. A divergence names the note.
/// </summary>
[Collection("App")]
public class DecoderPortTests(AppFixture app)
{
    [Fact]
    public async Task The_js_decoder_agrees_with_the_csharp_fixture_note_for_note()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await (await browser.NewContextAsync()).NewPageAsync();
        await page.GotoAsync(app.BaseUrl);

        var decoded = await page.EvaluateAsync<JsonElement>(
            @"async fixtureJson => {
                const fixture = JSON.parse(fixtureJson);
                const module = await import('/js/basic-pitch-decoder.js');
                return module.decodeNotes(
                    fixture.onsets, fixture.frames, fixture.framesPerSecond, fixture.minMidi);
            }",
            BasicPitchFixture.ReadJson());

        var fixture = BasicPitchFixture.Load();
        Assert.Equal(fixture.ExpectedNotes.Length, decoded.GetArrayLength());

        var index = 0;
        foreach (var note in decoded.EnumerateArray())
        {
            var expected = fixture.ExpectedNotes[index];
            var pitch = note.GetProperty("midiPitch").GetInt32();
            var start = note.GetProperty("startSec").GetDouble();
            var duration = note.GetProperty("durationSec").GetDouble();
            var velocity = note.GetProperty("velocity").GetInt32();
            Assert.True(
                expected.MidiPitch == pitch
                && Math.Abs(expected.StartSec - start) < 1e-9
                && Math.Abs(expected.DurationSec - duration) < 1e-9
                && expected.Velocity == velocity,
                $"Note {index} diverged between C# and JS: expected {expected.MidiPitch} @ " +
                $"{expected.StartSec}s for {expected.DurationSec}s vel {expected.Velocity}, " +
                $"got {pitch} @ {start}s for {duration}s vel {velocity}.");
            index++;
        }
    }
}
