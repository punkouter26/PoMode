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
/// </summary>
public sealed record BeatGridDto(double Bpm, double FirstBeatSec, double Confidence);
