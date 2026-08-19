namespace PoMode.Shared.Analysis;

public sealed record NoteEvent(int MidiPitch, double StartSec, double DurationSec, int Velocity);

public sealed record ChordSpan(string Symbol, string Root, string Quality, double StartSec, double EndSec);

/// <summary>
/// A fast first look at an upload (preview.json), computed from raw chroma and onset energy
/// before the real pipeline stages run. Provisional by definition — the full analysis replaces
/// it — so the client labels it "first look" and gates on <see cref="Confidence"/>.
/// </summary>
public sealed record AnalysisPreviewDto(string TonicName, double TempoBpm, double Confidence);

/// <summary>
/// The song's beat grid for the client metronome: beats fall at
/// <see cref="FirstBeatSec"/> + k·(60/<see cref="Bpm"/>). One tempo covers the whole song;
/// a low <see cref="Confidence"/> means "no usable beats" and the metronome stays unavailable.
///
/// <para>Deliberately still one tempo. <see cref="TempoMapDto"/> describes how the tempo actually
/// moves, but the metronome, the syncopation figure and beat-synchronous chord segmentation all
/// assume a single regular grid, so the map is reported alongside this rather than replacing it.</para>
/// </summary>
public sealed record BeatGridDto(double Bpm, double FirstBeatSec, double Confidence);

/// <summary>One measure's own tempo, read from the gap to the next downbeat.</summary>
/// <param name="Number">1-based, matching the measure numbers the canvas shows.</param>
/// <param name="Changed">Audibly different from the previous measure — the moment worth showing.</param>
public sealed record TempoMeasureDto(int Number, double StartSec, double Bpm, bool Changed);

/// <summary>
/// How the tempo moves across the song, measure by measure — for music that was played rather than
/// programmed, where a single BPM is an average of something that never actually happened.
///
/// <para><paramref name="IsSteady"/> is the headline: true and the per-measure list is noise around
/// one tempo, false and the list is the interesting part. <paramref name="Measures"/> is empty when
/// no beat grid could be trusted, which the client must show as "unknown" rather than as steady.</para>
/// </summary>
public sealed record TempoMapDto(
    double MedianBpm,
    double MinBpm,
    double MaxBpm,
    bool IsSteady,
    double Confidence,
    IReadOnlyList<TempoMeasureDto> Measures);
