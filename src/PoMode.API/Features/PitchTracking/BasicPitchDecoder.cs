using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// Converts Basic Pitch's raw onset/frame posterior arrays into discrete <see cref="NoteEvent"/>s.
/// Pure function — no model, no I/O — so it is fully unit-testable with hand-built arrays.
/// </summary>
public static class BasicPitchDecoder
{
    public static IReadOnlyList<NoteEvent> Decode(
        float[,] onsets,
        float[,] frames,
        double framesPerSecond,
        int minMidi,
        double onsetThreshold = 0.5,
        double frameThreshold = 0.3,
        double minDurationSec = 0.058)
    {
        var frameCount = onsets.GetLength(0);
        var pitchCount = onsets.GetLength(1);
        var frameDuration = 1.0 / framesPerSecond;
        var notes = new List<NoteEvent>();

        for (var bin = 0; bin < pitchCount; bin++)
        {
            int? start = null;
            double sumEnergy = 0;
            var count = 0;

            for (var f = 0; f < frameCount; f++)
            {
                var onsetFires = onsets[f, bin] >= onsetThreshold;

                if (start is not null && onsetFires)
                {
                    Emit(notes, bin, minMidi, start.Value, f, sumEnergy, count, frameDuration, minDurationSec);
                    start = null;
                    sumEnergy = 0;
                    count = 0;
                }

                if (start is null && onsetFires)
                {
                    start = f;
                }

                if (start is not null)
                {
                    if (frames[f, bin] >= frameThreshold)
                    {
                        sumEnergy += frames[f, bin];
                        count++;
                    }
                    else
                    {
                        Emit(notes, bin, minMidi, start.Value, f, sumEnergy, count, frameDuration, minDurationSec);
                        start = null;
                        sumEnergy = 0;
                        count = 0;
                    }
                }
            }

            if (start is not null)
            {
                Emit(notes, bin, minMidi, start.Value, frameCount, sumEnergy, count, frameDuration, minDurationSec);
            }
        }

        return [.. notes.OrderBy(n => n.StartSec).ThenBy(n => n.MidiPitch)];
    }

    private static void Emit(
        List<NoteEvent> notes,
        int bin,
        int minMidi,
        int startFrame,
        int endFrame,
        double sumEnergy,
        int count,
        double frameDuration,
        double minDurationSec)
    {
        var durationSec = (endFrame - startFrame) * frameDuration;
        if (durationSec < minDurationSec)
        {
            return;
        }

        var meanEnergy = count > 0 ? sumEnergy / count : 0;
        var velocity = Math.Clamp((int)(meanEnergy * 127), 1, 127);
        var startSec = startFrame * frameDuration;
        notes.Add(new NoteEvent(bin + minMidi, startSec, durationSec, velocity));
    }
}
