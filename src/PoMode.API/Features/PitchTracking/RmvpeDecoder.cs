using PoMode.Shared.Analysis;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// Turns RMVPE's per-frame pitch salience into <see cref="NoteEvent"/>s. Pure function — no model,
/// no I/O — so the decoding rules are unit-testable with hand-built salience.
///
/// <para>Two halves. The first is RMVPE's own decoder, reproduced from the reference
/// (<c>RMVPE.to_local_average_cents</c> in RVC): 360 bins 20 cents apart starting at
/// 1997.38 cents above 10 Hz, a salience-weighted average over the ±4 bins around the peak, and a
/// frame counts as voiced when its peak salience clears a threshold. The second half is ours: a
/// continuous pitch track is not a list of notes, and a sung line wobbles — vibrato alone swings
/// ±30–60 cents — so rounding each frame to the nearest semitone would split one held note into a
/// stutter of neighbours. Notes are cut with hysteresis instead: a note only changes when the pitch
/// has moved clearly into another semitone.</para>
/// </summary>
public static class RmvpeDecoder
{
    public const int Bins = 360;

    /// <summary>RMVPE's hop: 160 samples at 16 kHz.</summary>
    public const double FrameSeconds = 0.01;

    private const double CentsPerBin = 20.0;
    private const double FirstBinCents = 1997.3794084376191;

    /// <summary>MIDI number of the 10 Hz reference RMVPE's cents are measured from.</summary>
    private static readonly double ReferenceMidi = 69 + (12 * Math.Log2(10.0 / 440.0));

    /// <summary>
    /// Peak salience a frame needs to count as voiced. RVC uses 0.03 because voice conversion wants
    /// a pitch wherever one can be guessed; a transcription wants the opposite, since every
    /// breath or bleed frame promoted to a pitch becomes a note the reader sees. RMVPE's salience on
    /// a sung frame sits near 1 and on silence near 0, so the middle rejects noise without losing
    /// singing.
    /// </summary>
    public const double VoicingThreshold = 0.5;

    /// <summary>How far past the half-semitone boundary the pitch must go before the note changes.</summary>
    private const double HysteresisSemitones = 0.25;

    /// <summary>Unvoiced gaps this short inside one pitch are a tracking dropout, not a new note.</summary>
    private const int BridgeFrames = 3;

    /// <summary>Shorter than this is a scoop or a glitch rather than a note.</summary>
    private const double MinNoteSeconds = 0.07;

    /// <summary>Per-frame pitch in fractional MIDI, or null when unvoiced.</summary>
    /// <param name="salience">Row-major <c>[frames × 360]</c>.</param>
    public static double?[] DecodePitch(float[] salience, int frameCount, double threshold = VoicingThreshold)
    {
        var pitches = new double?[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            var row = frame * Bins;
            var peak = 0;
            for (var bin = 1; bin < Bins; bin++)
            {
                if (salience[row + bin] > salience[row + peak])
                {
                    peak = bin;
                }
            }
            if (salience[row + peak] <= threshold)
            {
                continue;
            }

            double weighted = 0, total = 0;
            for (var bin = Math.Max(0, peak - 4); bin <= Math.Min(Bins - 1, peak + 4); bin++)
            {
                var weight = salience[row + bin];
                weighted += weight * (FirstBinCents + (CentsPerBin * bin));
                total += weight;
            }
            if (total > 0)
            {
                pitches[frame] = ReferenceMidi + (weighted / total / 100.0);
            }
        }
        return pitches;
    }

    /// <summary>
    /// Segments a pitch track into notes. <paramref name="loudness"/> (per-frame RMS) only sets
    /// velocity; <paramref name="tuningOffsetCents"/> is subtracted first so a whole recording
    /// sitting sharp or flat lands back on the grid without changing any interval.
    /// </summary>
    public static IReadOnlyList<NoteEvent> Segment(
        double?[] pitches, double[] loudness, double tuningOffsetCents = 0.0)
    {
        var smoothed = MedianSmooth(pitches, tuningOffsetCents / 100.0);

        // Hysteresis quantisation: each voiced frame gets the note it belongs to.
        var notesPerFrame = new int?[smoothed.Length];
        int? current = null;
        for (var i = 0; i < smoothed.Length; i++)
        {
            if (smoothed[i] is not { } midi)
            {
                continue;
            }
            if (current is not { } held || Math.Abs(midi - held) > 0.5 + HysteresisSemitones)
            {
                current = (int)Math.Round(midi);
            }
            notesPerFrame[i] = current;
        }

        // Bridge short dropouts inside one pitch.
        for (var i = 0; i < notesPerFrame.Length; i++)
        {
            if (notesPerFrame[i] is not null || i == 0 || notesPerFrame[i - 1] is not { } before)
            {
                continue;
            }
            var end = i;
            while (end < notesPerFrame.Length && notesPerFrame[end] is null)
            {
                end++;
            }
            if (end - i <= BridgeFrames && end < notesPerFrame.Length && notesPerFrame[end] == before)
            {
                for (var j = i; j < end; j++)
                {
                    notesPerFrame[j] = before;
                }
            }
            i = end - 1;
        }

        var maxLoudness = 0.0;
        for (var i = 0; i < notesPerFrame.Length && i < loudness.Length; i++)
        {
            if (notesPerFrame[i] is not null)
            {
                maxLoudness = Math.Max(maxLoudness, loudness[i]);
            }
        }

        var notes = new List<NoteEvent>();
        var runStart = -1;
        for (var i = 0; i <= notesPerFrame.Length; i++)
        {
            var pitch = i < notesPerFrame.Length ? notesPerFrame[i] : null;
            if (runStart >= 0 && pitch != notesPerFrame[runStart])
            {
                Emit(notes, runStart, i, notesPerFrame[runStart]!.Value, loudness, maxLoudness);
                runStart = -1;
            }
            if (pitch is not null && runStart < 0)
            {
                runStart = i;
            }
        }
        return notes;
    }

    /// <summary>Median of the voiced ±2 neighbourhood — the cheap defence against one-frame octave
    /// blips — with the tuning shift applied on the way through.</summary>
    private static double?[] MedianSmooth(double?[] pitches, double shiftSemitones)
    {
        var smoothed = new double?[pitches.Length];
        var window = new List<double>(5);
        for (var i = 0; i < pitches.Length; i++)
        {
            if (pitches[i] is null)
            {
                continue;
            }
            window.Clear();
            for (var j = Math.Max(0, i - 2); j <= Math.Min(pitches.Length - 1, i + 2); j++)
            {
                if (pitches[j] is { } value)
                {
                    window.Add(value);
                }
            }
            window.Sort();
            smoothed[i] = window[window.Count / 2] - shiftSemitones;
        }
        return smoothed;
    }

    private static void Emit(
        List<NoteEvent> notes, int startFrame, int endFrame, int pitch, double[] loudness, double maxLoudness)
    {
        var durationSec = (endFrame - startFrame) * FrameSeconds;
        if (durationSec < MinNoteSeconds)
        {
            return;
        }
        var sum = 0.0;
        var count = 0;
        for (var i = startFrame; i < endFrame && i < loudness.Length; i++)
        {
            sum += loudness[i];
            count++;
        }
        var velocity = maxLoudness > 0 && count > 0
            ? Math.Clamp((int)Math.Round(127 * Math.Sqrt(sum / count / maxLoudness)), 20, 127)
            : 90;
        notes.Add(new NoteEvent(Math.Clamp(pitch, 0, 127), startFrame * FrameSeconds, durationSec, velocity));
    }
}
