using System.Globalization;
using System.Text;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ChordChart;

/// <summary>
/// Renders the detected chords as a plain-text lead-sheet chart: four 4/4 measures per line,
/// each measure labelled with the chord sounding at its midpoint ("." = the previous chord is
/// still ringing, "N.C." = nothing detected there). Uses the same 4/4 lead-sheet approximation
/// as the MIDI marker track and the metronome accents.
/// </summary>
public static class ChordChartBuilder
{
    private const int MeasuresPerLine = 4;

    /// <summary>Wide enough for the longest symbol the recognizer emits ("A#m" + headroom).</summary>
    private const int CellWidth = 5;

    public static string Build(IReadOnlyList<ChordSpan> chords, ModalResult result, string songName)
    {
        var chart = new StringBuilder();
        chart.AppendLine(songName);
        chart.AppendLine($"Key: {result.TonicName} {result.PrimaryMode?.ToString() ?? "(mode unclear)"}");
        chart.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Tempo: {result.TempoBpm:0} BPM{(result.TempoEstimated ? " (estimated)" : "")} · 4/4"));
        chart.AppendLine();

        if (chords.Count == 0)
        {
            chart.AppendLine("No chords were detected in this song.");
            return chart.ToString();
        }

        var bpm = result.TempoBpm <= 0 ? 120.0 : result.TempoBpm;
        var secondsPerMeasure = 4.0 * 60.0 / bpm;
        var measureCount = Math.Max(1, (int)Math.Ceiling(chords[^1].EndSec / secondsPerMeasure));

        string? previousSymbol = null;
        for (var measure = 0; measure < measureCount; measure++)
        {
            if (measure % MeasuresPerLine == 0)
            {
                chart.Append('|');
            }
            var midpoint = (measure + 0.5) * secondsPerMeasure;
            var index = TimelineSearch.IndexCovering(chords, midpoint, c => c.StartSec, c => c.EndSec);
            var symbol = index is null ? "N.C." : chords[index.Value].Symbol;
            var cell = symbol == previousSymbol ? "." : symbol;
            previousSymbol = symbol;
            chart.Append(' ').Append(cell.PadRight(CellWidth)).Append('|');
            if ((measure + 1) % MeasuresPerLine == 0)
            {
                chart.AppendLine();
            }
        }
        if (measureCount % MeasuresPerLine != 0)
        {
            chart.AppendLine();
        }
        return chart.ToString();
    }
}
