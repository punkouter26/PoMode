namespace PoMode.Shared.Analysis;

public sealed record NoteEvent(int MidiPitch, double StartSec, double DurationSec, int Velocity);

public sealed record ChordSpan(string Symbol, string Root, string Quality, double StartSec, double EndSec);
