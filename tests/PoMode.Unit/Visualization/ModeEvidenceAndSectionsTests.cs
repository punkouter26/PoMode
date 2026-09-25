using PoMode.API.Features.Analysis;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.SongStructure;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.Visualization;

/// <summary>
/// The "why this mode?" evidence and the section ribbon, both read off one synthetic song per case:
/// D-rooted, 2-second bars (120 BPM, no beat grid), each letter of <c>form</c> an 8-bar block. An A
/// block alternates the mode's tonic triad with its characteristic chord; a B block alternates the
/// triads on the mode's 3rd and 6th degrees. The melody sings the tonic, the 3rd and the mode's
/// headline tone in every bar and never the rival's tone, so the rival must be ruled out outright.
/// </summary>
public class ModeEvidenceAndSectionsTests
{
    private const int Tonic = 2; // D
    private const double BarSec = 2.0;
    private const int BarsPerLetter = 8;

    [Theory]
    [InlineData(ScaleMode.Dorian, "AABA", "Aeolian", "ABA")]
    [InlineData(ScaleMode.Mixolydian, "ABAB", "Ionian", "ABAB")]
    [InlineData(ScaleMode.Phrygian, "AAB", "Aeolian", "AB")]
    [InlineData(ScaleMode.Lydian, "AA", "Ionian", "")]
    public void The_payload_explains_the_mode_and_letters_the_form(
        ScaleMode mode, string form, string rival, string expectedLetters)
    {
        var scale = ScaleModes.Intervals(mode);
        var headline = ModeEvidenceBuilder.ContrastsFor(mode)[0];
        var signature = ModeEvidenceBuilder.CharacteristicChord(mode)!.Value;
        var chordA = new[] { Triad(scale, 0), Triad(scale, scale.ToList().IndexOf(signature.RootInterval)) };
        var chordB = new[] { Triad(scale, 2), Triad(scale, 5) };

        var chords = new List<ChordSpan>();
        var notes = new List<NoteEvent>();
        var windows = new List<ModalWindow>();
        var sung = new[] { 0, scale[2], headline.Tone };
        var mask = sung.Aggregate(0, (bits, interval) => bits | (1 << interval));
        foreach (var letter in form)
        {
            var pair = letter == 'A' ? chordA : chordB;
            for (var bar = 0; bar < BarsPerLetter; bar++)
            {
                var start = chords.Count * BarSec;
                var (root, quality) = pair[bar % 2];
                var name = ScaleModes.NoteName(root);
                chords.Add(new ChordSpan(name, name, quality, start, start + BarSec));
                windows.Add(new ModalWindow(windows.Count, start, start + BarSec, name, chords.Count, mask, sung, false,
                    [new ModalMatch(mode, 1.0, sung, [])]));
                for (var i = 0; i < sung.Length; i++)
                {
                    notes.Add(new NoteEvent(62 + sung[i], start + (i * 0.6), 0.5, 90));
                }
            }
        }
        var result = new ModalResult(1, Tonic, "D", 0.9, mode, 1.0, 120.0, false, windows);

        var payload = VisualizationBuilder.Build(notes, chords, result);

        // Evidence: the headline rival is named, ruled out outright, and nothing argues against it.
        var evidence = payload.Evidence!;
        Assert.Equal(rival, evidence.Contrasts[0].Rival);
        Assert.True(evidence.Contrasts[0].RulesOut);
        Assert.Contains(evidence.Reasons, reason => reason.Contains($"rules {rival} out"));
        Assert.Null(evidence.CounterEvidence);
        // The canvas rings every note on the headline tone and never the tonic, which proves nothing.
        Assert.All(payload.Notes, note =>
        {
            var interval = PitchNames.IntervalAboveTonic(note.MidiPitch, Tonic);
            if (interval == headline.Tone) Assert.True(note.Evidence);
            if (interval == 0) Assert.False(note.Evidence);
        });
        // A window that sang the tone and not the rival's says so on its own.
        Assert.All(payload.Windows, window => Assert.Contains($"rules out {rival}", window.Evidence));

        // Sections: repeated harmony shares a letter, adjacent repeats merge, a one-section song has none.
        Assert.Equal(expectedLetters, string.Concat(payload.Sections.Select(section => section.Letter)));
        Assert.All(payload.Sections, section =>
        {
            Assert.Equal(mode.ToString(), section.Mode);
            Assert.Equal($"--pm-mode-{mode.ToString().ToLowerInvariant()}", section.ColourToken);
            // Only the A blocks play the mode's own chord, so only they may name it.
            Assert.Equal(section.Letter == "A", section.Label.Contains($"({signature.Numeral} chord)"));
        });
        if (payload.Sections.Count > 0)
        {
            Assert.Equal(0.0, payload.Sections[0].StartSec);
            Assert.Equal(chords[^1].EndSec, payload.Sections[^1].EndSec);
        }

        // The "how were the sections found?" data is the analysis the ribbon was cut from: one cell
        // per pair of bars, every bar identical to itself, and a boundary under every section start.
        var structure = SongStructureEndpoints.ToDto(SongSectionBuilder.Analyse(chords, result)!);
        var bars = form.Length * BarsPerLetter;
        Assert.Equal(bars, structure.Novelty.Count);
        Assert.Equal(bars + 1, structure.BarStarts.Count);
        var cells = Convert.FromBase64String(structure.Similarity);
        Assert.Equal(bars * bars, cells.Length);
        Assert.All(Enumerable.Range(0, bars), bar => Assert.Equal(255, cells[(bar * bars) + bar]));
        Assert.All(payload.Sections.Skip(1), section =>
            Assert.Contains(section.StartSec, structure.Boundaries.Select(bar => structure.BarStarts[bar])));
        Assert.Equal(payload.Sections.Select(section => section.Letter), structure.Sections.Select(section => section.Letter));
    }

    /// <summary>The diatonic triad on a degree of the mode, as a D-rooted pitch class and quality.</summary>
    private static (int Root, string Quality) Triad(IReadOnlyList<int> scale, int degree)
    {
        var root = scale[degree];
        var third = (scale[(degree + 2) % 7] - root + 12) % 12;
        var fifth = (scale[(degree + 4) % 7] - root + 12) % 12;
        var quality = fifth == 6 ? "dim" : third == 3 ? "min" : "maj";
        return ((Tonic + root) % 12, quality);
    }
}
