using System.Xml.Linq;
using PoMode.API.Features.MusicXml;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.MusicXml;

public sealed class MusicXmlBuilderTests
{
    private static ModalResult Result(double tempo = 120) => new(
        SchemaVersion: 1,
        TonicPitchClass: 0,
        TonicName: "C",
        TonicConfidence: 0.9,
        PrimaryMode: ScaleMode.Ionian,
        PrimaryConfidence: 0.8,
        TempoBpm: tempo,
        TempoEstimated: false,
        Windows: []);

    [Fact]
    public void Builds_parseable_partwise_score_with_notes_and_harmony()
    {
        List<NoteEvent> notes = [new(60, 0.0, 0.5, 90), new(64, 0.5, 0.5, 90), new(67, 1.0, 1.0, 90)];
        List<ChordSpan> chords = [new("C", "C", "maj", 0.0, 2.0), new("Am", "A", "min", 2.0, 4.0)];

        var xml = XDocument.Parse(MusicXmlBuilder.Build(notes, chords, Result(), "Test Track"));

        var root = xml.Root!;
        Assert.Equal("score-partwise", root.Name.LocalName);
        Assert.Equal("Test Track", root.Element("work")!.Element("work-title")!.Value);
        Assert.Equal(3, root.Descendants("pitch").Count());
        Assert.Equal(2, root.Descendants("harmony").Count());
    }

    // Roman-numeral text is covered by RomanNumeralsTests; the export endpoint itself is covered
    // by E2EAPI. What stays here is the document shape no other test asserts.
    [Fact]
    public void First_measure_declares_key_time_and_clef()
    {
        List<NoteEvent> notes = [new(61, 0.0, 0.5, 90)]; // C#4 — sharp spelling

        var xml = XDocument.Parse(MusicXmlBuilder.Build(notes, [], Result(), "t"));

        var attributes = xml.Descendants("measure").First().Element("attributes")!;
        Assert.Equal("0", attributes.Element("key")!.Element("fifths")!.Value);
        Assert.Equal("major", attributes.Element("key")!.Element("mode")!.Value);
        Assert.Equal("4", attributes.Element("time")!.Element("beats")!.Value);
        Assert.Equal("G", attributes.Element("clef")!.Element("sign")!.Value);
        var pitch = xml.Descendants("pitch").Single();
        Assert.Equal("C", pitch.Element("step")!.Value);
        Assert.Equal("1", pitch.Element("alter")!.Value);
        Assert.Equal("4", pitch.Element("octave")!.Value);
    }
}
