using System.Xml.Linq;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.MusicXml;

/// <summary>
/// Builds a MusicXML 4.0 partwise lead sheet: melody notes, chord symbols (harmony elements)
/// and Roman-numeral annotations relative to the detected tonic. Measures are derived from the
/// detected tempo assuming 4/4 — a lead-sheet approximation, same as the MIDI export's.
/// </summary>
public static class MusicXmlBuilder
{
    private const int Divisions = 480; // ticks per quarter note
    private const int BeatsPerMeasure = 4;

    private static readonly string[] SharpNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static string Build(
        IReadOnlyList<NoteEvent> notes,
        IReadOnlyList<ChordSpan> chords,
        ModalResult result,
        string title)
    {
        var tempo = result.TempoBpm > 20 ? (int)Math.Round(result.TempoBpm) : 120;
        var secPerBeat = 60.0 / tempo;
        var secPerMeasure = secPerBeat * BeatsPerMeasure;

        var durationSec = Math.Max(
            notes.Count > 0 ? notes.Max(n => n.StartSec + n.DurationSec) : 0,
            chords.Count > 0 ? chords.Max(c => c.EndSec) : 0);
        var totalMeasures = Math.Max(1, (int)Math.Ceiling(durationSec / secPerMeasure));

        var score = new XElement("score-partwise", new XAttribute("version", "4.0"),
            new XElement("work", new XElement("work-title", title)),
            new XElement("identification",
                new XElement("creator", new XAttribute("type", "composer"), "PoMode Audio Modal Analyzer")),
            new XElement("part-list",
                new XElement("score-part", new XAttribute("id", "P1"),
                    new XElement("part-name", "Lead Vocal"),
                    new XElement("score-instrument", new XAttribute("id", "P1-I1"),
                        new XElement("instrument-name", "Voice")))));

        var part = new XElement("part", new XAttribute("id", "P1"));
        score.Add(part);

        for (var measure = 1; measure <= totalMeasures; measure++)
        {
            var startSec = (measure - 1) * secPerMeasure;
            var endSec = measure * secPerMeasure;
            var measureElement = new XElement("measure", new XAttribute("number", measure));
            part.Add(measureElement);

            if (measure == 1)
            {
                measureElement.Add(new XElement("attributes",
                    new XElement("divisions", Divisions),
                    new XElement("key",
                        new XElement("fifths", KeyFifths(result.TonicName)),
                        new XElement("mode", IsMinorMode(result.PrimaryMode) ? "minor" : "major")),
                    new XElement("time",
                        new XElement("beats", BeatsPerMeasure),
                        new XElement("beat-type", 4)),
                    new XElement("clef",
                        new XElement("sign", "G"),
                        new XElement("line", 2))));
                measureElement.Add(new XElement("direction", new XAttribute("placement", "above"),
                    new XElement("direction-type",
                        new XElement("metronome",
                            new XElement("beat-unit", "quarter"),
                            new XElement("per-minute", tempo))),
                    new XElement("sound", new XAttribute("tempo", tempo))));
            }

            foreach (var chord in chords.Where(c => c.StartSec >= startSec && c.StartSec < endSec))
            {
                AddHarmony(measureElement, chord, result.TonicPitchClass);
            }

            var measureNotes = notes
                .Where(n => n.StartSec >= startSec && n.StartSec < endSec)
                .OrderBy(n => n.StartSec)
                .ToList();
            if (measureNotes.Count == 0)
            {
                measureElement.Add(new XElement("note",
                    new XElement("rest", new XAttribute("measure", "yes")),
                    new XElement("duration", Divisions * BeatsPerMeasure),
                    new XElement("voice", 1)));
                continue;
            }
            foreach (var note in measureNotes)
            {
                measureElement.Add(BuildNote(note, secPerBeat));
            }
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "no"),
            new XDocumentType("score-partwise", "-//Recordare//DTD MusicXML 4.0 Partwise//EN",
                "http://www.musicxml.org/dtds/partwise.dtd", null),
            score);
        return doc.ToString();
    }

    private static void AddHarmony(XElement measure, ChordSpan chord, int tonicPitchClass)
    {
        var rootStep = chord.Root.TrimEnd('#', 'b', '♯', '♭');
        if (rootStep.Length != 1 || !char.IsAsciiLetterUpper(rootStep[0]))
        {
            return; // "N" (no chord) has no harmony element
        }
        var alter = chord.Root.Contains('#') || chord.Root.Contains('♯') ? 1
            : chord.Root.Contains('b') || chord.Root.Contains('♭') ? -1 : 0;
        var kind = chord.Quality.ToLowerInvariant() switch
        {
            "maj" or "" => "major",
            "min" or "m" => "minor",
            "7" => "dominant",
            "maj7" => "major-seventh",
            "min7" or "m7" => "minor-seventh",
            "dim" => "diminished",
            "sus4" => "suspended-fourth",
            "sus2" => "suspended-second",
            _ => "other",
        };
        measure.Add(new XElement("harmony",
            new XElement("root",
                new XElement("root-step", rootStep),
                alter != 0 ? new XElement("root-alter", alter) : null),
            new XElement("kind", new XAttribute("text", chord.Symbol), kind)));
        measure.Add(new XElement("direction", new XAttribute("placement", "below"),
            new XElement("direction-type",
                new XElement("words", new XAttribute("font-style", "italic"),
                    RomanNumerals.Analyze(chord, tonicPitchClass)))));
    }

    private static XElement BuildNote(NoteEvent note, double secPerBeat)
    {
        var name = SharpNames[((note.MidiPitch % 12) + 12) % 12];
        var octave = note.MidiPitch / 12 - 1;
        var ticks = Math.Max(Divisions / 4, (int)Math.Round(note.DurationSec / secPerBeat * Divisions));
        return new XElement("note",
            new XElement("pitch",
                new XElement("step", name[..1]),
                name.Length > 1 ? new XElement("alter", 1) : null,
                new XElement("octave", octave)),
            new XElement("duration", ticks),
            new XElement("voice", 1),
            new XElement("type", NoteType(ticks)));
    }

    private static string NoteType(int ticks) => ticks switch
    {
        >= Divisions * 4 => "whole",
        >= Divisions * 2 => "half",
        >= Divisions => "quarter",
        >= Divisions / 2 => "eighth",
        _ => "16th",
    };

    private static int KeyFifths(string tonicName) => tonicName.ToUpperInvariant() switch
    {
        "C" => 0,
        "G" => 1,
        "D" => 2,
        "A" => 3,
        "E" => 4,
        "B" => 5,
        "F#" or "GB" => 6,
        "F" => -1,
        "BB" or "A#" => -2,
        "EB" or "D#" => -3,
        "AB" or "G#" => -4,
        "DB" or "C#" => -5,
        _ => 0,
    };

    private static bool IsMinorMode(ScaleMode? mode) => mode is ScaleMode.Dorian
        or ScaleMode.Phrygian
        or ScaleMode.Aeolian
        or ScaleMode.Locrian
        or ScaleMode.MinorPentatonic;
}
