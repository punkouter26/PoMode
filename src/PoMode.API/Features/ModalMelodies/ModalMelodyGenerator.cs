using PoMode.API.Features.ChordRecognition;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.Visualization;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.ModalMelodies;

public sealed class ModalMelodyGenerator
{
    private static readonly IReadOnlyList<ChordProgressionDefinition> Presets =
    [
        new(
            Id: "pop-axis",
            Name: "Axis of Pop (C - G - Am - F / I - V - vi - IV)",
            Category: "Pop & Anthems",
            RomanNumerals: "I - V - vi - IV",
            ChordFormulas: ["I:maj", "V:maj", "vi:min", "IV:maj"],
            Description: "The iconic 4-chord progression of modern pop hits ('Let It Be', 'Don't Stop Believin'', 'Someone Like You').",
            SuggestedMode: ScaleMode.Ionian,
            DefaultBpm: 104.0),

        new(
            Id: "doo-wop",
            Name: "50s Doo-Wop (C - Am - F - G / I - vi - IV - V)",
            Category: "Pop & Anthems",
            RomanNumerals: "I - vi - IV - V",
            ChordFormulas: ["I:maj", "vi:min", "IV:maj", "V:maj"],
            Description: "Timeless vintage vocal progression heard in 1950s love songs ('Stand by Me', 'Earth Angel').",
            SuggestedMode: ScaleMode.Ionian,
            DefaultBpm: 92.0),

        new(
            Id: "sentimental",
            Name: "Emotional Ballad (Am - F - C - G / vi - IV - I - V)",
            Category: "Pop & Anthems",
            RomanNumerals: "vi - IV - I - V",
            ChordFormulas: ["vi:min", "IV:maj", "I:maj", "V:maj"],
            Description: "Dramatic, emotionally charged progression starting in relative minor ('Apologize', 'Numb').",
            SuggestedMode: ScaleMode.Aeolian,
            DefaultBpm: 88.0),

        new(
            Id: "canon",
            Name: "Pachelbel Canon (C - G - Am - Em / I - V - vi - iii)",
            Category: "Pop & Anthems",
            RomanNumerals: "I - V - vi - iii",
            ChordFormulas: ["I:maj", "V:maj", "vi:min", "iii:min"],
            Description: "The classical descending harmonic sequence that underpins countless pop anthems ('Memories', 'Basket Case').",
            SuggestedMode: ScaleMode.Ionian,
            DefaultBpm: 96.0),

        new(
            Id: "royal-road",
            Name: "Royal Road (F - G - Em - Am / IV - V - iii - vi)",
            Category: "Pop & Anthems",
            RomanNumerals: "IV - V - iii - vi",
            ChordFormulas: ["IV:maj", "V:maj", "iii:min", "vi:min"],
            Description: "The beloved 'Oudou' Japanese pop and anime chord progression with uplifting resolution.",
            SuggestedMode: ScaleMode.Ionian,
            DefaultBpm: 108.0),

        new(
            Id: "mixolydian-rock",
            Name: "Classic Rock Vamp (I - bVII - IV - I)",
            Category: "Modal & Grooves",
            RomanNumerals: "I - bVII - IV - I",
            ChordFormulas: ["I:maj", "bVII:maj", "IV:maj", "I:maj"],
            Description: "The driving classic rock and Southern rock groove powered by the flat 7th ('Sweet Home Alabama', 'Hey Jude').",
            SuggestedMode: ScaleMode.Mixolydian,
            DefaultBpm: 116.0,
            RootsOn: HarmonicRoot.ModeRoot,
            IsModeSignature: true),

        new(
            Id: "dorian-vamp",
            Name: "Dorian Groove (i - IV - i - IV)",
            Category: "Modal & Grooves",
            RomanNumerals: "i - IV - i - IV",
            ChordFormulas: ["i:min", "IV:maj", "i:min", "IV:maj"],
            Description: "The legendary Dorian vamp ('Oye Como Va', 'So What', 'Get Lucky') powered by the natural 6th.",
            SuggestedMode: ScaleMode.Dorian,
            DefaultBpm: 112.0,
            RootsOn: HarmonicRoot.ModeRoot,
            IsModeSignature: true),

        new(
            Id: "phrygian-tension",
            Name: "Phrygian Tension (i - bII - bVII - i)",
            Category: "Modal & Grooves",
            RomanNumerals: "i - bII - bVII - i",
            ChordFormulas: ["i:min", "bII:maj", "bVII:min", "i:min"],
            Description: "Dark Spanish flamenco and cinematic tension featuring the ominous minor 2nd half-step.",
            SuggestedMode: ScaleMode.Phrygian,
            DefaultBpm: 96.0,
            RootsOn: HarmonicRoot.ModeRoot,
            IsModeSignature: true),

        new(
            Id: "lydian-space",
            Name: "Lydian Wonder (I - II - I - II)",
            Category: "Modal & Grooves",
            RomanNumerals: "I - II - I - II",
            ChordFormulas: ["I:maj", "II:maj", "I:maj", "II:maj"],
            Description: "Cinematic sci-fi wonder and floating dreamscapes featuring the raised sharp 4th (#4).",
            SuggestedMode: ScaleMode.Lydian,
            DefaultBpm: 84.0,
            RootsOn: HarmonicRoot.ModeRoot,
            IsModeSignature: true),

        new(
            Id: "andalusian",
            Name: "Andalusian Descent (i - bVII - bVI - V)",
            Category: "Modal & Grooves",
            RomanNumerals: "i - bVII - bVI - V",
            ChordFormulas: ["i:min", "bVII:maj", "bVI:maj", "V:maj"],
            Description: "Dramatic flamenco descent with powerful harmonic resolution ('Sultans of Swing', 'Hit the Road Jack').",
            SuggestedMode: ScaleMode.Aeolian,
            DefaultBpm: 100.0,
            RootsOn: HarmonicRoot.ModeRoot),

        // Mode Signatures: one cadence per mode, counted from that mode's own tonic. Each is still
        // built only from the parent key's seven notes, which is the whole point — the note set never
        // changes, only which note the harmony treats as home. That is what makes a mode audible.
        new(
            Id: "mode-ionian",
            Name: "Ionian Cadence (I - IV - V - I)",
            Category: "Mode Signatures",
            RomanNumerals: "I - IV - V - I",
            ChordFormulas: ["I:maj", "IV:maj", "V:maj", "I:maj"],
            Description: "The plain major cadence. The leading tone pulls back to the tonic, so nothing sounds unresolved.",
            SuggestedMode: ScaleMode.Ionian,
            DefaultBpm: 104.0,
            RootsOn: HarmonicRoot.ModeRoot,
            IsModeSignature: true),

        new(
            Id: "mode-aeolian",
            Name: "Aeolian Lament (i - bVI - bIII - bVII)",
            Category: "Mode Signatures",
            RomanNumerals: "i - bVI - bIII - bVII",
            ChordFormulas: ["i:min", "bVI:maj", "bIII:maj", "bVII:maj"],
            Description: "Natural minor with no raised leading tone, so it falls away from home instead of resolving to it.",
            SuggestedMode: ScaleMode.Aeolian,
            DefaultBpm: 88.0,
            RootsOn: HarmonicRoot.ModeRoot,
            IsModeSignature: true),

        new(
            Id: "mode-locrian",
            Name: "Locrian Unrest (i dim - bII - bV - i dim)",
            Category: "Mode Signatures",
            RomanNumerals: "i(dim) - bII - bV - i(dim)",
            ChordFormulas: ["i:dim", "bII:maj", "bV:maj", "i:dim"],
            Description: "The one mode whose own tonic chord is diminished. The flat 5th denies it a stable home, which is why it is heard as tension rather than a key.",
            SuggestedMode: ScaleMode.Locrian,
            DefaultBpm: 92.0,
            RootsOn: HarmonicRoot.ModeRoot,
            IsModeSignature: true),

        new(
            Id: "mode-major-pentatonic",
            Name: "Pentatonic Open Air (I - IV - V - I)",
            Category: "Mode Signatures",
            RomanNumerals: "I - IV - V - I",
            ChordFormulas: ["I:maj", "IV:maj", "V:maj", "I:maj"],
            Description: "Major harmony under a five-note melody. Dropping the 4th and 7th removes every half step, so no melody note can clash.",
            SuggestedMode: ScaleMode.MajorPentatonic,
            DefaultBpm: 100.0,
            RootsOn: HarmonicRoot.ModeRoot,
            IsModeSignature: true),

        new(
            Id: "mode-minor-pentatonic",
            Name: "Blues Pentatonic (i - bIII - bVII - i)",
            Category: "Mode Signatures",
            RomanNumerals: "i - bIII - bVII - i",
            ChordFormulas: ["i:min", "bIII:maj", "bVII:maj", "i:min"],
            Description: "The rock and blues vocal scale over its own minor tonic, leaning on the flat 3rd and flat 7th.",
            SuggestedMode: ScaleMode.MinorPentatonic,
            DefaultBpm: 108.0,
            RootsOn: HarmonicRoot.ModeRoot,
            IsModeSignature: true),

        new(
            Id: "jazz-turnaround",
            Name: "Jazz Cadence (Dm7 - G7 - Cmaj7 - A7 / ii - V - I - VI)",
            Category: "Jazz & Blues",
            RomanNumerals: "ii7 - V7 - Imaj7 - VI7",
            ChordFormulas: ["ii:min7", "V:dom7", "I:maj7", "VI:dom7"],
            Description: "The fundamental jazz standard cycle of fifths turnaround.",
            SuggestedMode: ScaleMode.Dorian,
            DefaultBpm: 120.0),

        new(
            Id: "blues-turn",
            Name: "12-Bar Blues Turn (C7 - F7 - C7 - G7 / I7 - IV7 - I7 - V7)",
            Category: "Jazz & Blues",
            RomanNumerals: "I7 - IV7 - I7 - V7",
            ChordFormulas: ["I:dom7", "IV:dom7", "I:dom7", "V:dom7"],
            Description: "Dominant 7th blues turnaround suited for expressive minor pentatonic and mixolydian vocals.",
            SuggestedMode: ScaleMode.Mixolydian,
            DefaultBpm: 108.0),
    ];

    public IReadOnlyList<ChordProgressionDefinition> GetProgressions() => Presets;

    public ChordProgressionDefinition GetProgression(string id)
        => Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Presets[0];

    public GeneratedMelodyDto Generate(ModalMelodyRequest request)
    {
        var progression = GetProgression(request.ProgressionId);
        var bpm = request.Bpm is >= 40 and <= 240 ? request.Bpm : progression.DefaultBpm;
        var parentTonicClass = ((request.TonicPitchClass % 12) + 12) % 12;
        var parentTonicName = PitchNames.Name(parentTonicClass);

        var modeDegreeOffset = ScaleModes.ModeDegreeOffset(request.Mode);
        var modeRootClass = (parentTonicClass + modeDegreeOffset) % 12;
        var modeRootName = PitchNames.Name(modeRootClass);

        // 0 is a real setting now, not a floor: at the bottom of the slider the melody stops anchoring
        // to the mode root and the loop stops opening on the home chord.
        var targetPurity = Math.Clamp(request.TargetPurity, 0.0, 100.0);

        var chords = DelayTheHomeChord(
            BuildChordSpans(progression, parentTonicClass, modeRootClass, bpm), modeRootClass, targetPurity);
        var backingNotes = ChordPadBuilder.Build(chords);
        var melodyNotes = GenerateRelativeModalMelodyNotes(
            parentTonicClass: parentTonicClass,
            mode: request.Mode,
            chords: chords,
            bpm: bpm,
            style: request.Style,
            seed: request.Seed,
            baseOctave: Math.Clamp(request.Octave, 3, 5),
            targetPurity: targetPurity);

        var modePercentage = CalculateModePercentage(parentTonicClass, request.Mode, melodyNotes);
        var modalResult = BuildModalResult(modeRootClass, modeRootName, request.Mode, bpm, chords, melodyNotes);
        var visual = VisualizationBuilder.Build(melodyNotes, chords, modalResult);

        return new GeneratedMelodyDto(
            ProgressionId: progression.Id,
            ProgressionName: progression.Name,
            TonicPitchClass: modeRootClass,
            TonicName: $"{parentTonicName} (Mode root: {modeRootName})",
            Mode: request.Mode,
            Bpm: bpm,
            Style: request.Style,
            Seed: request.Seed,
            ModePercentage: modePercentage,
            CharacteristicExplanation: ExplainRelativeMode(parentTonicName, request.Mode),
            ScaleNotes: ScaleModes.RelativeScaleNoteNames(parentTonicClass, request.Mode),
            MelodyNotes: melodyNotes,
            BackingNotes: backingNotes,
            Chords: chords,
            Visual: visual,
            ModalAnalysis: modalResult,
            BetterFit: FindBetterFit(request.Mode, modeRootClass, melodyNotes, modePercentage));
    }

    /// <summary>How far ahead a rival mode has to score before it is worth naming.</summary>
    private const double BetterFitMarginPoints = 2.0;

    /// <summary>
    /// Scores the melody against every mode on every root and returns the winner, but only when it
    /// beats the mode that was asked for by more than <see cref="BetterFitMarginPoints"/>. At a loose
    /// purity setting the melody stops anchoring to its own tonic, and because the relative modes all
    /// draw on one set of notes, what comes out often has a genuinely different home note. Saying so is
    /// more honest than reporting a low score against a mode the melody is no longer really in.
    /// </summary>
    private static ModeFitDto? FindBetterFit(
        ScaleMode requestedMode,
        int requestedRootClass,
        IReadOnlyList<NoteEvent> melodyNotes,
        double requestedScore)
    {
        if (melodyNotes.Count == 0) return null;

        // Every pitch class the melody actually sounds. A rival reading has to account for all of them:
        // scale adherence is only part of the score, so without this the winner can be a mode built on
        // notes the melody never plays, named on the strength of where it happens to start and end.
        var sounded = 0;
        foreach (var note in melodyNotes)
        {
            sounded |= 1 << (((note.MidiPitch % 12) + 12) % 12);
        }

        ModeFitDto? best = null;
        foreach (var mode in Enum.GetValues<ScaleMode>())
        {
            // A mode's root is fixed by the parent it is measured from, so walking all twelve parents
            // covers every root exactly once.
            for (var parentClass = 0; parentClass < 12; parentClass++)
            {
                var rootClass = (parentClass + ScaleModes.ModeDegreeOffset(mode)) % 12;
                if (mode == requestedMode && rootClass == requestedRootClass) continue;

                // On the same root, a scale built from a subset of the requested one is not a rival
                // reading, just the same music described with fewer notes. A major melody that happens
                // to skip its 4th and 7th would otherwise always be announced as pentatonic, which says
                // nothing about where home is.
                if (rootClass == requestedRootClass
                    && (ModeDefinitions.Mask(mode) & ~ModeDefinitions.Mask(requestedMode)) == 0)
                {
                    continue;
                }

                if ((sounded & ~AbsoluteScaleMask(mode, rootClass)) != 0) continue;

                var score = CalculateModePercentage(parentClass, mode, melodyNotes);
                if (best is null || score > best.Percentage)
                {
                    best = new ModeFitDto(mode, rootClass, $"{PitchNames.Name(rootClass)} {mode}", score);
                }
            }
        }

        return best is not null && best.Percentage > requestedScore + BetterFitMarginPoints ? best : null;
    }

    /// <summary>The mode's pitch classes as a 12-bit mask, placed on <paramref name="rootClass"/>.</summary>
    private static int AbsoluteScaleMask(ScaleMode mode, int rootClass)
    {
        var mask = 0;
        foreach (var interval in ScaleModes.Intervals(mode))
        {
            mask |= 1 << ((rootClass + interval) % 12);
        }
        return mask;
    }

    /// <summary>
    /// Computes the modal purity / affinity percentage (0.0% to 100.0%) of the melody for the target mode.
    /// Evaluates: In-Mode note duration ratio (40 pts), Modal tonic anchoring &amp; cadence (30 pts),
    /// Characteristic color tone expression (20 pts), and vocal melodic singability (10 pts).
    /// </summary>
    public static double CalculateModePercentage(
        int parentTonicClass,
        ScaleMode mode,
        IReadOnlyList<NoteEvent> melodyNotes)
    {
        if (melodyNotes.Count == 0) return 0.0;

        var modeDegreeOffset = ScaleModes.ModeDegreeOffset(mode);
        var modeRootClass = (parentTonicClass + modeDegreeOffset) % 12;
        var modeMask = ModeDefinitions.Mask(mode);
        var charInterval = GetPrimaryCharacteristicInterval(mode);

        var totalDuration = melodyNotes.Sum(n => n.DurationSec);
        if (totalDuration <= 0) return 0.0;

        // 1. Scale In-Mode Adherence (0 to 40 pts)
        var inModeDuration = 0.0;
        foreach (var note in melodyNotes)
        {
            var interval = PitchNames.IntervalAboveTonic(note.MidiPitch, modeRootClass);
            if ((modeMask & (1 << interval)) != 0)
            {
                inModeDuration += note.DurationSec;
            }
        }
        var scaleAdherencePts = (inModeDuration / totalDuration) * 40.0;

        // 2. Modal Tonic Anchoring & Cadence (0 to 30 pts)
        var tonicPts = 0.0;
        var firstPitchClass = ((melodyNotes[0].MidiPitch % 12) + 12) % 12;
        if (firstPitchClass == modeRootClass) tonicPts += 10.0;

        var lastPitchClass = ((melodyNotes[^1].MidiPitch % 12) + 12) % 12;
        if (lastPitchClass == modeRootClass) tonicPts += 10.0;

        var tonicFifthDuration = melodyNotes
            .Where(n =>
            {
                var iv = PitchNames.IntervalAboveTonic(n.MidiPitch, modeRootClass);
                return iv == 0 || iv == 7; // Root or 5th
            })
            .Sum(n => n.DurationSec);
        tonicPts += Math.Min(10.0, (tonicFifthDuration / totalDuration) * 25.0);

        // 3. Characteristic Modal Color Note Expression (0 to 20 pts)
        var charNoteCount = melodyNotes.Count(n =>
            PitchNames.IntervalAboveTonic(n.MidiPitch, modeRootClass) == charInterval);
        var charPts = Math.Min(20.0, charNoteCount * 10.0);

        // 4. Vocal Melodic Stepwise Singability (0 to 10 pts)
        var stepwiseCount = 0;
        for (var i = 1; i < melodyNotes.Count; i++)
        {
            var step = Math.Abs(melodyNotes[i].MidiPitch - melodyNotes[i - 1].MidiPitch);
            if (step is 1 or 2 or 3 or 4) stepwiseCount++;
        }
        var singabilityPts = melodyNotes.Count > 1
            ? (stepwiseCount / (double)(melodyNotes.Count - 1)) * 10.0
            : 10.0;

        var totalPercentage = scaleAdherencePts + tonicPts + charPts + singabilityPts;
        return Math.Clamp(Math.Round(totalPercentage, 1), 0.0, 100.0);
    }

    /// <summary>
    /// Decides how plainly the loop states where home is. At the top of the purity slider the mode's
    /// own chord lands on bar one, which is the most direct way to name a tonal centre. Lower settings
    /// push it further into the loop, so the bar the ear lands on first is no longer home and the mode
    /// has to be heard from the melody and the colour chords instead.
    /// </summary>
    private static IReadOnlyList<ChordSpan> DelayTheHomeChord(
        IReadOnlyList<ChordSpan> chords, int modeRootClass, double targetPurity)
    {
        if (chords.Count < 2) return chords;

        var delay = Math.Clamp(
            (int)Math.Floor((100.0 - targetPurity) / 100.0 * chords.Count), 0, chords.Count - 1);
        if (delay == 0) return chords;

        // Rotating reuses the exact same chords, so no setting of the slider can push the harmony out
        // of the mode. Skip a rotation that would land on home anyway: an alternating vamp comes back
        // to itself every other step, and the point of the setting is to open somewhere else.
        for (var attempt = 0; attempt < chords.Count; attempt++)
        {
            var shift = (delay + attempt) % chords.Count;
            if (shift == 0) continue;

            var rotated = RotateOverTimeSlots(chords, shift);
            if (!(PitchNames.TryParseRoot(rotated[0].Root, out var openingRoot) && openingRoot == modeRootClass))
            {
                return rotated;
            }
        }

        return chords;
    }

    /// <summary>Reorders the chords by <paramref name="shift"/> while the measure boundaries stay put.</summary>
    private static IReadOnlyList<ChordSpan> RotateOverTimeSlots(IReadOnlyList<ChordSpan> chords, int shift)
    {
        var count = chords.Count;
        var rotated = new List<ChordSpan>(count);
        for (var i = 0; i < count; i++)
        {
            var source = chords[(((i - shift) % count) + count) % count];
            rotated.Add(source with { StartSec = chords[i].StartSec, EndSec = chords[i].EndSec });
        }
        return rotated;
    }

    /// <summary>
    /// Lays the progression out over one measure per chord. Which pitch the roman numerals count up
    /// from is the progression's own choice: a pop progression counts from the parent key, a modal one
    /// from the mode root, so that i really is the mode's tonic.
    /// </summary>
    private static IReadOnlyList<ChordSpan> BuildChordSpans(
        ChordProgressionDefinition progression,
        int parentTonicClass,
        int modeRootClass,
        double bpm)
    {
        var tonicClass = progression.RootsOn == HarmonicRoot.ModeRoot ? modeRootClass : parentTonicClass;
        var secondsPerBeat = 60.0 / bpm;
        var secondsPerMeasure = secondsPerBeat * 4.0;
        var chords = new List<ChordSpan>();

        for (var i = 0; i < progression.ChordFormulas.Count; i++)
        {
            var formula = progression.ChordFormulas[i];
            var parts = formula.Split(':');
            var numeral = parts[0];
            var quality = parts.Length > 1 ? parts[1] : "maj";

            var semitoneOffset = ParseRomanOffset(numeral);
            var rootClass = (tonicClass + semitoneOffset + 12) % 12;
            var rootName = PitchNames.Name(rootClass);

            var symbolSuffix = quality switch
            {
                "min" => "m",
                "min7" => "m7",
                "maj7" => "maj7",
                "dom7" => "7",
                "dim" => "dim",
                "aug" => "aug",
                _ => string.Empty,
            };

            var symbol = rootName + symbolSuffix;
            var startSec = i * secondsPerMeasure;
            var endSec = (i + 1) * secondsPerMeasure;

            chords.Add(new ChordSpan(symbol, rootName, quality, startSec, endSec));
        }

        return chords;
    }

    private static int ParseRomanOffset(string numeral) => numeral switch
    {
        "I" or "i" => 0,
        "bII" or "bii" => 1,
        "II" or "ii" => 2,
        "bIII" or "biii" => 3,
        "III" or "iii" => 4,
        "IV" or "iv" => 5,
        "#IV" or "#iv" or "bV" or "bv" => 6,
        "V" or "v" => 7,
        "bVI" or "bvi" => 8,
        "VI" or "vi" => 9,
        "bVII" or "bvii" => 10,
        "VII" or "vii" => 11,
        _ => 0,
    };

    private static int GetPrimaryCharacteristicInterval(ScaleMode mode) => mode switch
    {
        ScaleMode.Lydian => 6,           // #4 (Augmented 4th, e.g. B in F Lydian)
        ScaleMode.Ionian => 11,         // Natural 7th (e.g. B in C Ionian)
        ScaleMode.Mixolydian => 10,     // b7 (Flat 7th, e.g. F in G Mixolydian)
        ScaleMode.Dorian => 9,          // Natural 6th (e.g. B in D Dorian)
        ScaleMode.Aeolian => 8,         // b6 (Minor 6th, e.g. F in A Aeolian)
        ScaleMode.Phrygian => 1,        // b2 (Minor 2nd, e.g. F in E Phrygian)
        ScaleMode.Locrian => 6,         // b5 (Diminished 5th, e.g. F in B Locrian)
        ScaleMode.MajorPentatonic => 4, // Major 3rd
        ScaleMode.MinorPentatonic => 3, // Minor 3rd
        _ => 0,
    };

    /// <summary>
    /// Generates melodies that strictly use ONLY the notes of the parent scale (e.g. C Major: C D E F G A B),
    /// adjusted by the desired target purity (40% to 100%) to control modal tonic anchoring and characteristic color prominence.
    /// </summary>
    private static IReadOnlyList<NoteEvent> GenerateRelativeModalMelodyNotes(
        int parentTonicClass,
        ScaleMode mode,
        IReadOnlyList<ChordSpan> chords,
        double bpm,
        MelodyStyle style,
        int seed,
        int baseOctave,
        double targetPurity = 90.0)
    {
        var modeDegreeOffset = ScaleModes.ModeDegreeOffset(mode);
        var modeRootClass = (parentTonicClass + modeDegreeOffset) % 12;
        var parentTonicMidi = (baseOctave + 1) * 12 + parentTonicClass; // e.g. Octave 4 C = 60
        var modeRootMidi = (baseOctave + 1) * 12 + modeRootClass;
        var secondsPerBeat = 60.0 / bpm;
        var rng = new Random(seed ^ ((int)mode * 29) ^ (int)(targetPurity * 13));

        // Pitch pool: the mode's own scale across the comfortable vocal range (C4 to A5). For the seven
        // diatonic modes this is the parent major scale re-spelled from the mode root, so nothing
        // changes. For the pentatonics it is the five notes they are actually made of — drawing from
        // all seven let a "Major Pentatonic" melody sound the 4th and 7th the scale exists to omit.
        var pitchPool = new List<int>();
        var modeIntervals = ScaleModes.Intervals(mode);
        for (var oct = baseOctave; oct <= baseOctave + 1; oct++)
        {
            foreach (var iv in modeIntervals)
            {
                var pitch = (oct + 1) * 12 + ((modeRootClass + iv) % 12);
                if (pitch >= parentTonicMidi - 2 && pitch <= parentTonicMidi + 21) // Bb3 to A5
                {
                    if (!pitchPool.Contains(pitch))
                    {
                        pitchPool.Add(pitch);
                    }
                }
            }
        }
        pitchPool.Sort();

        // Mode root pitches (e.g. D4, D5 for D Dorian in C Major)
        var modeRootPitches = pitchPool.Where(p => ((p % 12) + 12) % 12 == modeRootClass).ToList();
        if (modeRootPitches.Count == 0) modeRootPitches.Add(modeRootMidi);

        // Characteristic color pitches (e.g. B4, B5 for D Dorian in C Major)
        var charInterval = GetPrimaryCharacteristicInterval(mode);
        var charPitches = pitchPool.Where(p => PitchNames.IntervalAboveTonic(p, modeRootClass) == charInterval).ToList();
        if (charPitches.Count == 0) charPitches = modeRootPitches;

        // Purity thresholds:
        var mustStartOnRoot = targetPurity >= 80.0 || rng.NextDouble() < (targetPurity / 100.0);
        var mustCadenceOnRoot = targetPurity >= 75.0 || rng.NextDouble() < (targetPurity / 100.0);
        var charToneProbability = Math.Clamp((targetPurity - 30.0) / 70.0, 0.1, 0.95);

        var notes = new List<NoteEvent>();
        var currentPitch = mustStartOnRoot ? modeRootPitches[0] : ClosestPitch(pitchPool, modeRootMidi + 4, rng);

        for (var measure = 0; measure < chords.Count; measure++)
        {
            var chord = chords[measure];
            PitchNames.TryParseRoot(chord.Root, out var chordRootClass);
            var chordVoicing = ChordPadBuilder.VoicingFor(chord.Quality);
            var chordTonePitches = pitchPool.Where(p =>
            {
                var pitchClass = ((p % 12) + 12) % 12;
                var offset = (pitchClass - chordRootClass + 12) % 12;
                return chordVoicing.Contains(offset);
            }).ToList();

            if (chordTonePitches.Count == 0)
            {
                chordTonePitches.Add(currentPitch);
            }

            var measureStart = chord.StartSec;
            var isLastMeasure = measure == chords.Count - 1;

            switch (style)
            {
                case MelodyStyle.Arpeggiated:
                    var arpeggioCount = (measure % 2 == 1) ? 6 : 8;
                    for (var step = 0; step < arpeggioCount; step++)
                    {
                        var start = measureStart + (step * 0.5 * secondsPerBeat);
                        var dur = 0.44 * secondsPerBeat;
                        int nextPitch;

                        if (measure == 0 && step == 0 && mustStartOnRoot)
                        {
                            nextPitch = modeRootPitches[0];
                        }
                        else if (isLastMeasure && step >= arpeggioCount - 2 && mustCadenceOnRoot)
                        {
                            nextPitch = modeRootPitches[0];
                        }
                        else if (measure == 2 && step == 2 && rng.NextDouble() < charToneProbability)
                        {
                            nextPitch = charPitches[rng.Next(charPitches.Count)];
                        }
                        else if (step % 2 == 0)
                        {
                            nextPitch = ClosestPitch(chordTonePitches, currentPitch, rng);
                        }
                        else
                        {
                            nextPitch = StepwiseVocalPitch(pitchPool, currentPitch, rng);
                        }

                        currentPitch = nextPitch;
                        var velocity = step % 4 == 0 ? 100 : (step % 2 == 0 ? 90 : 78);
                        notes.Add(new NoteEvent(nextPitch, start, dur, velocity));
                    }
                    break;

                case MelodyStyle.Syncopated:
                    var syncSteps = (measure % 2 == 0)
                        ? new[] { (0.0, 1.25, true), (1.5, 0.85, false), (2.5, 1.20, true) }
                        : new[] { (0.0, 0.90, true), (1.0, 0.45, false), (1.5, 1.40, true) };

                    for (var s = 0; s < syncSteps.Length; s++)
                    {
                        var (beat, len, isStrong) = syncSteps[s];
                        var start = measureStart + (beat * secondsPerBeat);
                        var dur = len * secondsPerBeat;
                        int nextPitch;

                        if (measure == 0 && s == 0 && mustStartOnRoot)
                        {
                            nextPitch = modeRootPitches[0];
                        }
                        else if (isLastMeasure && s == syncSteps.Length - 1 && mustCadenceOnRoot)
                        {
                            nextPitch = modeRootPitches[0];
                        }
                        else if (measure == 2 && isStrong && rng.NextDouble() < charToneProbability)
                        {
                            nextPitch = charPitches[rng.Next(charPitches.Count)];
                        }
                        else if (isStrong)
                        {
                            nextPitch = ClosestPitch(chordTonePitches, currentPitch, rng);
                        }
                        else
                        {
                            nextPitch = StepwiseVocalPitch(pitchPool, currentPitch, rng);
                        }

                        currentPitch = nextPitch;
                        notes.Add(new NoteEvent(nextPitch, start, dur, isStrong ? 98 : 84));
                    }
                    break;

                case MelodyStyle.Motific:
                    var motifSteps = isLastMeasure
                        ? new[] { (0.0, 1.40, true), (1.5, 0.50, false), (2.0, 1.75, true) }
                        : new[] { (0.0, 1.35, true), (1.5, 0.45, false), (2.0, 1.60, true) };

                    for (var m = 0; m < motifSteps.Length; m++)
                    {
                        var (beat, len, isStrong) = motifSteps[m];
                        var start = measureStart + (beat * secondsPerBeat);
                        var dur = len * secondsPerBeat;
                        int nextPitch;

                        if (measure == 0 && m == 0 && mustStartOnRoot)
                        {
                            nextPitch = modeRootPitches[0];
                        }
                        else if (isLastMeasure && m == motifSteps.Length - 1 && mustCadenceOnRoot)
                        {
                            nextPitch = modeRootPitches[0];
                        }
                        else if (measure == 2 && isStrong && rng.NextDouble() < charToneProbability)
                        {
                            nextPitch = charPitches[rng.Next(charPitches.Count)];
                        }
                        else if (isStrong)
                        {
                            nextPitch = ClosestPitch(chordTonePitches, currentPitch, rng);
                        }
                        else
                        {
                            nextPitch = StepwiseVocalPitch(pitchPool, currentPitch, rng);
                        }

                        currentPitch = nextPitch;
                        notes.Add(new NoteEvent(nextPitch, start, dur, isStrong ? 96 : 82));
                    }
                    break;

                case MelodyStyle.Lyrical:
                default:
                    var beats = (measure % 2 == 0)
                        ? new[] { (0.0, 0.95), (1.0, 0.95), (2.0, 1.65) }
                        : (isLastMeasure
                            ? new[] { (0.0, 1.25), (1.5, 0.50), (2.0, 1.80) }
                            : new[] { (0.0, 1.25), (1.5, 0.50), (2.0, 0.95), (3.0, 0.85) });

                    for (var i = 0; i < beats.Length; i++)
                    {
                        var (bStart, bLen) = beats[i];
                        var start = measureStart + (bStart * secondsPerBeat);
                        var dur = bLen * secondsPerBeat;

                        int nextPitch;
                        if (measure == 0 && i == 0 && mustStartOnRoot)
                        {
                            nextPitch = modeRootPitches[0];
                        }
                        else if (isLastMeasure && i == beats.Length - 1 && mustCadenceOnRoot)
                        {
                            nextPitch = modeRootPitches[0];
                        }
                        else if (measure == 2 && i == 1 && rng.NextDouble() < charToneProbability)
                        {
                            nextPitch = charPitches[rng.Next(charPitches.Count)];
                        }
                        else if (i == 0)
                        {
                            nextPitch = ClosestPitch(chordTonePitches, currentPitch, rng);
                        }
                        else
                        {
                            nextPitch = StepwiseVocalPitch(pitchPool, currentPitch, rng);
                        }

                        currentPitch = nextPitch;
                        var vel = i == 0 ? 102 : (i == beats.Length - 1 ? 88 : 94);
                        notes.Add(new NoteEvent(nextPitch, start, dur, vel));
                    }
                    break;
            }
        }

        return notes;
    }

    private static int ClosestPitch(IReadOnlyList<int> candidates, int target, Random rng)
    {
        if (candidates.Count == 0) return target;
        var ordered = candidates.OrderBy(c => Math.Abs(c - target)).ToList();
        return ordered.Count > 1 && rng.NextDouble() < 0.25 ? ordered[1] : ordered[0];
    }

    private static int StepwiseVocalPitch(List<int> scale, int current, Random rng)
    {
        var idx = scale.IndexOf(current);
        if (idx < 0)
        {
            var closest = scale.OrderBy(s => Math.Abs(s - current)).First();
            idx = scale.IndexOf(closest);
        }

        var delta = rng.NextDouble() < 0.75
            ? (rng.Next(0, 2) == 0 ? 1 : -1)
            : (rng.Next(0, 2) == 0 ? 2 : -2);

        var nextIdx = Math.Clamp(idx + delta, 0, scale.Count - 1);
        return scale[nextIdx];
    }

    public static string ExplainRelativeMode(string parentKey, ScaleMode mode)
    {
        return mode switch
        {
            ScaleMode.Ionian => $"{parentKey} Ionian (1st Degree): Starts on {parentKey}. Uses the exact 7 notes of {parentKey} Major, creating a triumphant, fully resolved major sound.",
            ScaleMode.Dorian => $"Dorian (2nd Degree): Starts on the 2nd note of {parentKey} Major. Uses 100% {parentKey} scale notes, giving a soulful jazz/minor mood with a natural 6th.",
            ScaleMode.Phrygian => $"Phrygian (3rd Degree): Starts on the 3rd note of {parentKey} Major. Uses 100% {parentKey} scale notes, giving a Spanish flamenco feel with a half-step minor 2nd.",
            ScaleMode.Lydian => $"Lydian (4th Degree): Starts on the 4th note of {parentKey} Major. Uses 100% {parentKey} scale notes, giving an ethereal, floating feel with a raised 4th.",
            ScaleMode.Mixolydian => $"Mixolydian (5th Degree): Starts on the 5th note of {parentKey} Major. Uses 100% {parentKey} scale notes, giving a classic rock/blues feel with a flat 7th.",
            ScaleMode.Aeolian => $"Aeolian (6th Degree): Starts on the 6th note of {parentKey} Major (the Relative Minor). Uses 100% {parentKey} scale notes for a deeply emotional minor mood.",
            ScaleMode.Locrian => $"Locrian (7th Degree): Starts on the 7th note of {parentKey} Major. Uses 100% {parentKey} scale notes, creating a tense, unresolved diminished mood.",
            ScaleMode.MajorPentatonic => $"Major Pentatonic: Uses 5 core notes of {parentKey} Major (1-2-3-5-6), leaving zero harmonic clash.",
            ScaleMode.MinorPentatonic => $"Relative Minor Pentatonic: Uses 5 notes starting on the 6th degree, producing the standard blues/rock vocal scale.",
            _ => "Relative mode scale melody exploration.",
        };
    }

    private static ModalResult BuildModalResult(
        int tonicClass,
        string tonicName,
        ScaleMode mode,
        double bpm,
        IReadOnlyList<ChordSpan> chords,
        IReadOnlyList<NoteEvent> melodyNotes)
    {
        var windows = new List<ModalWindow>();
        for (var i = 0; i < chords.Count; i++)
        {
            var chord = chords[i];
            var measureNotes = melodyNotes
                .Where(n => n.StartSec >= chord.StartSec - 0.01 && n.StartSec < chord.EndSec)
                .ToList();

            var intervals = measureNotes
                .Select(n => PitchNames.IntervalAboveTonic(n.MidiPitch, tonicClass))
                .Distinct()
                .OrderBy(v => v)
                .ToList();

            var mask = 0;
            foreach (var iv in intervals)
            {
                mask |= (1 << iv);
            }

            var match = new ModalMatch(
                Mode: mode,
                Confidence: 0.95,
                MatchedIntervals: intervals,
                OutsideIntervals: []);

            windows.Add(new ModalWindow(
                Index: i,
                StartSec: chord.StartSec,
                EndSec: chord.EndSec,
                ChordSymbol: chord.Symbol,
                MeasureNumber: i + 1,
                VocalMask: mask,
                SungIntervals: intervals,
                InsufficientEvidence: intervals.Count < 2,
                Matches: [match]));
        }

        return new ModalResult(
            SchemaVersion: 1,
            TonicPitchClass: tonicClass,
            TonicName: tonicName,
            TonicConfidence: 0.99,
            PrimaryMode: mode,
            PrimaryConfidence: 0.96,
            TempoBpm: bpm,
            TempoEstimated: false,
            Windows: windows);
    }
}
