using System.Globalization;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Analysis;

/// <summary>
/// "Songs like this" within one library: which other analysed songs share this one's modal colour.
/// A song's profile is the share of its timeline each mode won (window length weighted), because two
/// songs feel alike when they spend their time in the same modes, whatever key they are in. Tempo and
/// a shared tonic only break near-ties. Pure arithmetic over result.json, so it is tested directly.
/// </summary>
public static class SongSimilarity
{
    private static readonly int ModeCount = Enum.GetValues<ScaleMode>().Length;

    /// <summary>Below this a pairing is noise, not likeness, and is not offered.</summary>
    public const double MinScore = 0.5;

    /// <summary>Each mode's share of the song's decided windows, by time. All zero when no window
    /// was decided, which matches nothing.</summary>
    public static double[] Profile(ModalResult result)
    {
        var shares = new double[ModeCount];
        foreach (var window in result.Windows)
        {
            if (window is { InsufficientEvidence: false, Matches.Count: > 0 })
            {
                shares[(int)window.Matches[0].Mode] += Math.Max(window.EndSec - window.StartSec, 0);
            }
        }
        var total = shares.Sum();
        if (total > 0)
        {
            for (var i = 0; i < shares.Length; i++)
            {
                shares[i] /= total;
            }
        }
        return shares;
    }

    /// <summary>0..1: modal colour (cosine of the profiles) dominates; tempo within 40 BPM and a
    /// shared tonic add a little.</summary>
    public static double Score(ModalResult a, ModalResult b)
    {
        var (pa, pb) = (Profile(a), Profile(b));
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < pa.Length; i++)
        {
            dot += pa[i] * pb[i];
            na += pa[i] * pa[i];
            nb += pb[i] * pb[i];
        }
        var modes = na > 0 && nb > 0 ? dot / Math.Sqrt(na * nb) : 0;
        var tempo = Math.Max(0, 1 - (Math.Abs(a.TempoBpm - b.TempoBpm) / 40));
        var tonic = a.TonicPitchClass == b.TonicPitchClass ? 1 : 0;
        return (0.75 * modes) + (0.15 * tempo) + (0.10 * tonic);
    }

    /// <summary>Why, in words, claiming only what holds: the shared main mode, how far apart the
    /// tempos are when that is close, and a shared key.</summary>
    public static string Reason(ModalResult song, ModalResult other)
    {
        var parts = new List<string>();
        if (song.PrimaryMode is { } mode && other.PrimaryMode == mode)
        {
            parts.Add($"mostly {ScaleModes.DisplayName(mode.ToString())} too");
        }
        else
        {
            var (ps, po) = (Profile(song), Profile(other));
            var overlap = ps.Zip(po, Math.Min).Sum();
            var overlapPercent = (overlap * 100).ToString("0", CultureInfo.InvariantCulture);
            parts.Add($"{overlapPercent}% of their time in the same modes");
        }
        var apart = Math.Abs(song.TempoBpm - other.TempoBpm);
        if (apart < 0.5)
        {
            parts.Add("same tempo");
        }
        else if (apart <= 10)
        {
            parts.Add($"{apart:0} BPM apart");
        }
        if (song.TonicPitchClass == other.TonicPitchClass)
        {
            parts.Add($"both on {other.TonicName}");
        }
        var sentence = string.Join(" · ", parts);
        return char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }
}
