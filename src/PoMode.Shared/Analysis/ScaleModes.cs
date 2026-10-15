namespace PoMode.Shared.Analysis;

/// <summary>
/// Pure scale-mode lookups: fixed music-theory data (interval sets, sharp-spelled pitch names)
/// that both the API and the Client render from. This is the deliberate Shared carve-out — no
/// I/O, no state, no musical judgment; the API's <c>ModeDefinitions</c> layers its scoring
/// weights on top of these interval sets.
/// </summary>
public static class ScaleModes
{
    private static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>The seven diatonic letters, for spelling a pitch by letter rather than by pitch class.</summary>
    private static readonly string[] Letters = ["C", "D", "E", "F", "G", "A", "B"];

    /// <summary>Pitch class each diatonic letter sits at in its natural form.</summary>
    private static readonly int[] LetterPitchClasses = [0, 2, 4, 5, 7, 9, 11];

    /// <summary>
    /// The keys written with flats: F, Bb, Eb, Ab, Db and Gb. Everything else is a sharp key. This is
    /// the usual convention (six flats is the limit before enharmonic respelling) and it is what
    /// decides whether <see cref="NoteName(int,int)"/> writes Bb or A# for the same pitch.
    /// </summary>
    private static bool IsFlatKey(int tonicPitchClass) => Mod(tonicPitchClass) is 5 or 10 or 3 or 8 or 1 or 6;

    private static int Mod(int pitchClass) => ((pitchClass % 12) + 12) % 12;

    private static readonly Dictionary<ScaleMode, int[]> IntervalSets = new()
    {
        [ScaleMode.Ionian] = [0, 2, 4, 5, 7, 9, 11],
        [ScaleMode.Dorian] = [0, 2, 3, 5, 7, 9, 10],
        [ScaleMode.Phrygian] = [0, 1, 3, 5, 7, 8, 10],
        [ScaleMode.Lydian] = [0, 2, 4, 6, 7, 9, 11],
        [ScaleMode.Mixolydian] = [0, 2, 4, 5, 7, 9, 10],
        [ScaleMode.Aeolian] = [0, 2, 3, 5, 7, 8, 10],
        [ScaleMode.Locrian] = [0, 1, 3, 5, 6, 8, 10],
        [ScaleMode.MinorPentatonic] = [0, 3, 5, 7, 10],
        [ScaleMode.MajorPentatonic] = [0, 2, 4, 7, 9],
    };

    public static IReadOnlyList<ScaleMode> All { get; } = [.. IntervalSets.Keys];

    /// <summary>"MajorPentatonic" as a reader writes it. Results carry the enum name; every label shows this.</summary>
    public static string DisplayName(string? mode)
        => string.Concat((mode ?? "").Select((c, i) => i > 0 && char.IsUpper(c) ? $" {c}" : $"{c}"));

    /// <summary>Semitone offsets above the tonic, ascending, tonic (0) first.</summary>
    /// <summary>
    /// The degrees that separate a mode from its nearest neighbours, most telling first. Lives here
    /// rather than in the API because the client highlights them too, and three copies of this table
    /// is three chances for them to disagree.
    /// </summary>
    private static readonly Dictionary<ScaleMode, int[]> CharacteristicSets = new()
    {
        [ScaleMode.Ionian] = [11],           // major 7
        [ScaleMode.Dorian] = [9, 3],         // natural 6 over a minor 3
        [ScaleMode.Phrygian] = [1],          // flat 2
        [ScaleMode.Lydian] = [6],            // sharp 4
        [ScaleMode.Mixolydian] = [10, 4],    // flat 7 over a major 3
        [ScaleMode.Aeolian] = [8, 3],        // flat 6 over a minor 3
        [ScaleMode.Locrian] = [6, 1],        // flat 5 and flat 2
        [ScaleMode.MinorPentatonic] = [3, 10],
        [ScaleMode.MajorPentatonic] = [4, 9],
    };

    public static IReadOnlyList<int> CharacteristicIntervals(ScaleMode mode) => CharacteristicSets[mode];

    public static IReadOnlyList<int> Intervals(ScaleMode mode) => IntervalSets[mode];

    /// <summary>Sharp-spelled pitch-class name — the app's one naming convention for a bare pitch class.</summary>
        public static string NoteName(int pitchClass) => Names[Mod(pitchClass)];

        /// <summary>
        /// A pitch's name spelled to suit <paramref name="tonicPitchClass"/>'s key: flats in the six flat
        /// keys, sharps everywhere else. Without this a scale on F printed "F G A A# C D E" — an A#
        /// where every musician reads Bb — and a chord's root contradicted the flat in its own roman
        /// numeral. <see cref="NoteName(int)"/> stays sharp for the places that carry a pitch class with
        /// no key behind it (a DTO field, a log line).
        /// </summary>
        public static string NoteName(int pitchClass, int tonicPitchClass)
        {
            var pc = Mod(pitchClass);
            if (!IsFlatKey(tonicPitchClass))
            {
                return Names[pc];
            }
            // A flat key spells a black key as a flat. Ranking flat above natural above sharp means a
            // flat key reads Bb and Eb, while C stays C and the one pitch with no flat spelling yet.
            var bestLetter = -1;
            var bestAccidental = '\0';
            var bestRank = int.MaxValue;
            for (var letter = 0; letter < Letters.Length; letter++)
            {
                var offset = Mod(pc - LetterPitchClasses[letter]);
                var (accidental, rank) = offset switch
                {
                    11 => ('b', 0),
                    0 => ('\0', 1),
                    1 => ('#', 2),
                    _ => ('\0', int.MaxValue),
                };
                if (rank < bestRank)
                {
                    bestRank = rank;
                    bestLetter = letter;
                    bestAccidental = accidental;
                }
            }
            return bestLetter < 0 ? Names[pc] : $"{Letters[bestLetter]}{bestAccidental}";
        }

        /// <summary>
        /// The diatonic letter index (C=0 … B=6) a pitch class is spelled with on its own: pitch 10 is
        /// B flat, not A sharp. Chord roots are spelled from a letter rather than a pitch class, which is
        /// the only way a chord can agree with its roman numeral — G's flat five is D flat, even though
        /// a pitch class alone would have said C sharp.
        /// </summary>
        public static int LetterIndexOf(int pitchClass)
        {
            var letter = NoteName(pitchClass, pitchClass)[0];
            return Array.IndexOf(Letters, letter.ToString());
        }

        /// <summary>A diatonic letter plus the accidental to print on it: -1 flat, 1 sharp, 0 none.</summary>
        public static string SpellLetter(int letterIndex, int accidental)
            => $"{Letters[((letterIndex % 7) + 7) % 7]}{(accidental < 0 ? "b" : accidental > 0 ? "#" : "")}";

        /// <summary>The pitch class a diatonic letter sits at in its natural form (C=0 … B=11).</summary>
        public static int LetterPitchClass(int letterIndex)
            => LetterPitchClasses[((letterIndex % 7) + 7) % 7];

        /// <summary>The mode's note names built on <paramref name="tonicPitchClass"/>, tonic first.</summary>
        public static string[] NoteNames(int tonicPitchClass, ScaleMode mode)
            => [.. IntervalSets[mode].Select(interval => NoteName(tonicPitchClass + interval, tonicPitchClass))];

    /// <summary>Degree semitone offset of the relative mode in the parent major scale.</summary>
    public static int ModeDegreeOffset(ScaleMode mode) => mode switch
    {
        ScaleMode.Ionian => 0,          // 1st degree (e.g. C in C Major)
        ScaleMode.Dorian => 2,          // 2nd degree (e.g. D in C Major)
        ScaleMode.Phrygian => 4,        // 3rd degree (e.g. E in C Major)
        ScaleMode.Lydian => 5,          // 4th degree (e.g. F in C Major)
        ScaleMode.Mixolydian => 7,      // 5th degree (e.g. G in C Major)
        ScaleMode.Aeolian => 9,         // 6th degree (e.g. A in C Major)
        ScaleMode.Locrian => 11,        // 7th degree (e.g. B in C Major)
        ScaleMode.MajorPentatonic => 0, // Major Pentatonic (1-2-3-5-6)
        ScaleMode.MinorPentatonic => 9, // Relative Minor Pentatonic on Degree 6
        _ => 0,
    };

    /// <summary>
    /// Returns the notes of the parent key, ordered starting from the mode's root note.
    /// In C Major:
    /// C Ionian: C D E F G A B
    /// D Dorian: D E F G A B C
    /// E Phrygian: E F G A B C D
    /// F Lydian: F G A B C D E
    /// G Mixolydian: G A B C D E F
    /// A Aeolian: A B C D E F G
    /// B Locrian: B C D E F G A
    /// All notes belong 100% strictly to the parent major scale.
    /// </summary>
    public static string[] RelativeScaleNoteNames(int parentTonicPitchClass, ScaleMode mode)
    {
        var offset = ModeDegreeOffset(mode);
        var modeRootClass = (parentTonicPitchClass + offset) % 12;

        if (mode == ScaleMode.MajorPentatonic)
        {
            int[] pentaOffsets = [0, 2, 4, 7, 9];
            return [.. pentaOffsets.Select(iv => NoteName(parentTonicPitchClass + iv))];
        }
        if (mode == ScaleMode.MinorPentatonic)
        {
            int[] pentaOffsets = [0, 3, 5, 7, 10];
            return [.. pentaOffsets.Select(iv => NoteName(modeRootClass + iv))];
        }

        int[] parentIntervals = [0, 2, 4, 5, 7, 9, 11];
        var idx = Array.IndexOf(parentIntervals, offset);
        if (idx < 0) idx = 0;

        var result = new string[7];
        for (var i = 0; i < 7; i++)
        {
            var iv = parentIntervals[(idx + i) % 7];
            result[i] = NoteName(parentTonicPitchClass + iv);
        }
        return result;
    }

    /// <summary>Fractional MIDI number of a frequency (A4 = 440 Hz = 69) — the one converter,
    /// shared by the pitch tracker and the chroma extractor.</summary>
    public static double MidiFromFrequency(double frequencyHz)
        => 69 + (12 * Math.Log2(frequencyHz / 440.0));
}
