using PoMode.API.Audio;
using PoMode.API.Features.PitchTracking;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.VoiceProfile;

/// <summary>
/// How well each transcribed note was actually sung. notes.json keeps whole semitones — right for a
/// melody, useless for tuning — so this goes back to the audio and reads the continuous pitch inside
/// each note with YIN.
/// </summary>
public static class IntonationMeter
{
    /// <summary>A frame further than this from its note is the two trackers disagreeing (an octave
    /// slip, a consonant) rather than the singer being that far out, and is left out.</summary>
    private const double MaxDeviationSemitones = 1.0;

    public static IReadOnlyList<NoteIntonation> Measure(
        AudioBuffer audio, IReadOnlyList<NoteEvent> notes, double tuningOffsetCents)
    {
        var frames = YinMelodyTranscriber.PitchesInside(
            audio, [.. notes.Select(n => (n.StartSec, n.StartSec + n.DurationSec))], tuningOffsetCents);
        return [.. notes.Select((note, i) =>
        {
            var deviations = frames[i]
                .Select(midi => midi - note.MidiPitch)
                .Where(d => Math.Abs(d) <= MaxDeviationSemitones)
                .Order()
                .ToArray();
            if (deviations.Length < 2)
            {
                return new NoteIntonation(note.MidiPitch, note.DurationSec, null, null);
            }
            // Median for where the note sat, so one scooped frame cannot drag it; spread for how much it
            // moved while held, which is exactly what one scooped frame should count against.
            var median = deviations[deviations.Length / 2];
            var mean = deviations.Average();
            var spread = Math.Sqrt(deviations.Sum(d => (d - mean) * (d - mean)) / deviations.Length);
            return new NoteIntonation(note.MidiPitch, note.DurationSec, median * 100, spread * 100);
        })];
    }
}
