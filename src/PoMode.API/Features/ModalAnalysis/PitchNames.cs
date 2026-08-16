namespace PoMode.API.Features.ModalAnalysis;

public static class PitchNames
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static readonly string[] Degrees = ["1", "b2", "2", "b3", "3", "4", "#4", "5", "b6", "6", "b7", "7"];

    public static string Name(int pitchClass) => Names[((pitchClass % 12) + 12) % 12];

    public static string IntervalLabel(int semitones) => Degrees[((semitones % 12) + 12) % 12];
}
