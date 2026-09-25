using PoMode.API.Features.Analysis;
using PoMode.API.Features.ModalAnalysis;
using PoMode.API.Features.ModalMelodies;
using PoMode.Shared.Analysis;

namespace PoMode.API.Features.Demo;

/// <summary>
/// The first-run example: a short piece synthesized from the Mode Lab's own generator, so its mode is
/// known truth rather than somebody's opinion of a recording, and no audio file has to live in the
/// repository. Fixed request, fixed seeds — the same bytes on every server, every time.
/// </summary>
public static class DemoSong
{
    /// <summary>The template's well-known job id. Fixed so a restart finds the template it already
    /// built instead of building another; a valid 32-hex id so recovery treats it like any job.</summary>
    public const string TemplateJobId = "00000000000000000000000000de3001";

    /// <summary>Server-owned, so the template is in nobody's library and survives the purge sweeps.</summary>
    public const string TemplateOwner = JobState.ServerOwnerPrefix + "demo-template";

    /// <summary>
    /// F Lydian over its own signature vamp (C's seven notes, F as home, the raised fourth sounding in
    /// the II chord). A mode that is plainly not major or minor, so the example shows what the app is
    /// for; purity 100 keeps the melody on its own tonic, so it is a clear case, not a borderline one.
    ///
    /// <para>Not D Dorian, the obvious first choice: over the Dorian i–IV vamp the analyzer puts the
    /// tonic on G, even when handed the generator's exact notes — every seed and every melody style.
    /// F Lydian is read correctly for every seed tried, and a first-run example has to be a case the
    /// analyzer gets right, or its first lesson is that the answer is wrong.</para>
    /// </summary>
    private static readonly ModalMelodyRequest Request = new(
        TonicPitchClass: 0,
        Mode: ScaleMode.Lydian,
        ProgressionId: "lydian-space",
        Bpm: 84.0,
        Style: MelodyStyle.Lyrical,
        Seed: 7,
        TargetPurity: 100.0);

    /// <summary>Four passes of the vamp, about 45 seconds: enough bars for the canvas to pan and the
    /// mode timeline to have several windows per chord.</summary>
    private const int Passes = 4;

    /// <summary>
    /// The rendered demo: the mix the job is filed under, each part rendered on its own (the stems),
    /// the sentence that labels it, and the score it came from — everything here the server knows
    /// exactly because it wrote it. The synthesizer never turns a render up, only down to stop it
    /// clipping, so the stems play at their level in the mix.
    /// </summary>
    public sealed record Composition(
        string FileName,
        byte[] Mix,
        byte[] Melody,
        byte[] Backing,
        TakeOrigin Origin,
        IReadOnlyList<ChordSpan> Chords,
        double Bpm);

    public static Composition Compose(ModalMelodyGenerator generator)
    {
        var melody = new List<NoteEvent>();
        var chords = new List<ChordSpan>();
        GeneratedMelodyDto? first = null;
        var offset = 0.0;
        for (var pass = 0; pass < Passes; pass++)
        {
            // A new seed per pass: four identical bars repeated read as a loop test, a new phrase over
            // the same vamp reads as a tune. The chords do not depend on the seed, only the melody does.
            var generated = generator.Generate(Request with { Seed = Request.Seed + pass });
            first ??= generated;
            var shift = offset;
            melody.AddRange(generated.MelodyNotes.Select(note => note with { StartSec = note.StartSec + shift }));
            chords.AddRange(generated.Chords.Select(chord =>
                chord with { StartSec = chord.StartSec + shift, EndSec = chord.EndSec + shift }));
            offset += generated.Chords[^1].EndSec;
        }

        var mix = ModalWavSynthesizer.Synthesize(melody, chords, offset);
        var melodyOnly = ModalWavSynthesizer.Synthesize(melody, [], offset);
        var backingOnly = ModalWavSynthesizer.Synthesize([], chords, offset);
        var modeName = $"{PitchNames.Name(first!.TonicPitchClass)} {first.Mode}";
        // Unlike a hum take's origin, this one may name the mode outright: it is what the server
        // synthesized, a fact to check the analysis against, not a guess that would pre-empt it.
        var origin = new TakeOrigin(
            TakeOrigin.Demo, $"Demo · synthesized in {modeName} so you can see a finished analysis");
        return new Composition(
            $"Demo - {modeName}.wav", mix, melodyOnly, backingOnly, origin, chords, first.Bpm);
    }
}
