using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Visualization;

/// <summary>
/// Flattens the three analysis artifacts into the single payload the canvas draws (spec §7). Pure function —
/// no audio, no models, no I/O. Every musical decision happens here rather than in JS, so it is unit-testable
/// and cannot drift out of step with <see cref="ModeDefinitions"/>.
/// </summary>
public static class VisualizationBuilder
{
    private const int SchemaVersion = 1;

    /// <summary>Smallest pitch span the canvas can lay out without dividing by zero.</summary>
    private const int MinimumPitchSpan = 12;

    private const int DefaultMinPitch = 48; // C3
    private const int DefaultMaxPitch = 72; // C5

    public static VisualizationPayload Build(
        IReadOnlyList<NoteEvent> notes,
        IReadOnlyList<ChordSpan> chords,
        ModalResult result)
    {
        var visualNotes = new List<VisualNote>(notes.Count);
        foreach (var note in notes)
        {
            var interval = IntervalAboveTonic(note.MidiPitch, result.TonicPitchClass);
            visualNotes.Add(new VisualNote(
                MidiPitch: note.MidiPitch,
                StartSec: note.StartSec,
                DurationSec: note.DurationSec,
                Velocity: note.Velocity,
                Role: RoleOf(note, interval, chords, result),
                PitchLabel: PitchLabel(note.MidiPitch),
                DegreeLabel: $"[{PitchNames.IntervalLabel(interval)}]"));
        }

        var visualChords = new List<VisualChord>(chords.Count);
        foreach (var chord in chords)
        {
            var window = WindowCovering(result, chord.StartSec);
            visualChords.Add(new VisualChord(
                Symbol: chord.Symbol,
                StartSec: chord.StartSec,
                EndSec: chord.EndSec,
                MeasureNumber: window?.MeasureNumber ?? 1,
                ModeTag: TopMode(window)?.ToString()));
        }

        var visualWindows = new List<VisualWindow>(result.Windows.Count);
        foreach (var window in result.Windows)
        {
            visualWindows.Add(ToVisualWindow(window));
        }

        var (minPitch, maxPitch) = PitchRange(notes);
        var duration = Math.Max(
            notes.Count == 0 ? 0.0 : notes.Max(note => note.StartSec + note.DurationSec),
            chords.Count == 0 ? 0.0 : chords.Max(chord => chord.EndSec));

        return new VisualizationPayload(
            SchemaVersion, visualNotes, visualChords, visualWindows, duration, minPitch, maxPitch);
    }

    /// <summary>
    /// Projects a window into the HUD's view of it. Deliberately does NOT fall back to the whole-song
    /// primary mode when evidence was insufficient: the canvas may colour notes optimistically, but the
    /// HUD is a factual readout and must not claim a match the engine did not make.
    /// </summary>
    private static VisualWindow ToVisualWindow(ModalWindow window)
    {
        var top = TopMode(window);
        var modeMask = top is null ? 0 : ModeDefinitions.Mask(top.Value);
        IReadOnlyList<int> characteristic =
            top is null ? [] : ModeDefinitions.CharacteristicIntervals(top.Value);

        var degrees = new List<DegreeBadge>(12);
        for (var interval = 0; interval < 12; interval++)
        {
            degrees.Add(new DegreeBadge(
                Interval: interval,
                Label: PitchNames.IntervalLabel(interval),
                Sung: (window.VocalMask & (1 << interval)) != 0,
                InMode: (modeMask & (1 << interval)) != 0,
                Characteristic: characteristic.Contains(interval)));
        }

        var alternatives = window.Matches
            .Skip(1)
            .Select(match => new ModeAlternative(match.Mode.ToString(), match.Confidence))
            .ToArray();

        return new VisualWindow(
            Index: window.Index,
            StartSec: window.StartSec,
            EndSec: window.EndSec,
            ChordSymbol: window.ChordSymbol,
            MeasureNumber: window.MeasureNumber,
            MaskHex: $"0x{window.VocalMask:X3}",
            ModeTag: top?.ToString(),
            ModeConfidence: top is null ? null : window.Matches[0].Confidence,
            InsufficientEvidence: window.InsufficientEvidence,
            Degrees: degrees,
            Alternatives: alternatives);
    }

    /// <summary>
    /// Index of the window covering <paramref name="timeSec"/>, or null outside every window. Windows are
    /// half-open <c>[Start, End)</c>, so a boundary time belongs to the later window.
    /// </summary>
    public static int? WindowIndexAt(ModalResult result, double timeSec)
    {
        for (var index = 0; index < result.Windows.Count; index++)
        {
            var window = result.Windows[index];
            if (timeSec >= window.StartSec && timeSec < window.EndSec)
            {
                return index;
            }
        }
        return null;
    }

    /// <summary>
    /// Role rules in priority order (spec §7): chord tone, then characteristic degree, then in-mode, then
    /// outside. A note is judged against its own window's top-ranked mode; a window with insufficient
    /// evidence (or a note past the last window) falls back to the whole-song primary mode.
    /// </summary>
    private static NoteRole RoleOf(
        NoteEvent note,
        int interval,
        IReadOnlyList<ChordSpan> chords,
        ModalResult result)
    {
        if (IsChordTone(note.MidiPitch, ChordCovering(chords, note.StartSec)))
        {
            return NoteRole.ChordTone;
        }

        var mode = TopMode(WindowCovering(result, note.StartSec)) ?? result.PrimaryMode;
        if (mode is null)
        {
            return NoteRole.Outside;
        }

        if (ModeDefinitions.CharacteristicIntervals(mode.Value).Contains(interval))
        {
            return NoteRole.Characteristic;
        }

        return (ModeDefinitions.Mask(mode.Value) & (1 << interval)) != 0
            ? NoteRole.InMode
            : NoteRole.Outside;
    }

    private static bool IsChordTone(int midiPitch, ChordSpan? chord)
    {
        if (chord is null || !PitchNames.TryPitchClass(chord.Root, out var root))
        {
            return false;
        }

        var third = chord.Quality == "min" ? 3 : 4;
        var pitchClass = ((midiPitch % 12) + 12) % 12;
        return pitchClass == root % 12
            || pitchClass == (root + third) % 12
            || pitchClass == (root + 7) % 12;
    }

    private static ChordSpan? ChordCovering(IReadOnlyList<ChordSpan> chords, double timeSec)
        => chords.FirstOrDefault(chord => timeSec >= chord.StartSec && timeSec < chord.EndSec);

    private static ModalWindow? WindowCovering(ModalResult result, double timeSec)
        => WindowIndexAt(result, timeSec) is { } index ? result.Windows[index] : null;

    private static ScaleMode? TopMode(ModalWindow? window)
        => window is { InsufficientEvidence: false, Matches.Count: > 0 } ? window.Matches[0].Mode : null;

    private static int IntervalAboveTonic(int midiPitch, int tonicPitchClass)
        => ((((midiPitch % 12) + 12) % 12) - tonicPitchClass + 12) % 12;

    /// <summary>MIDI 60 is C4, so the octave is <c>midi / 12 - 1</c>.</summary>
    private static string PitchLabel(int midiPitch)
        => $"{PitchNames.Name(midiPitch)}{(midiPitch / 12) - 1}";

    private static (int Min, int Max) PitchRange(IReadOnlyList<NoteEvent> notes)
    {
        if (notes.Count == 0)
        {
            return (DefaultMinPitch, DefaultMaxPitch);
        }

        var min = notes.Min(note => note.MidiPitch);
        var max = notes.Max(note => note.MidiPitch);
        var shortfall = MinimumPitchSpan - (max - min);
        if (shortfall <= 0)
        {
            return (min, max);
        }

        // Pad symmetrically so a single-pitch or narrow track still gets a layoutable range.
        var below = shortfall / 2;
        return (min - below, max + (shortfall - below));
    }
}
