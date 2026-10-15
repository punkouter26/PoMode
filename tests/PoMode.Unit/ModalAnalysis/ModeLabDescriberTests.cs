using PoMode.API.Features.ModalMelodies;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.ModalAnalysis;

/// <summary>The Mode Lab by description: plain words move the settings they name, "in D" names the
/// mode's own home note, and a model's reply only counts what it actually changed.</summary>
public class ModeLabDescriberTests
{
    private static readonly ModalMelodyRequest Current = new(0, ScaleMode.Ionian, "pop-axis", 100, MelodyStyle.Lyrical, 42, 4, 90);

    [Fact]
    public void Words_move_the_settings_they_name_and_a_model_reply_counts_only_real_changes()
    {
        var (request, changes) = ModeLabDescriber.ByRules("darker and much slower, funky, in D", Current, newSeed: 7);
        // One step darker than Ionian is Mixolydian; D Mixolydian lives in the key of G.
        Assert.Equal(ScaleMode.Mixolydian, request.Mode);
        Assert.Equal(7, request.TonicPitchClass);
        Assert.Equal(70, request.Bpm);
        Assert.Equal(MelodyStyle.Syncopated, request.Style);
        Assert.Equal(4, changes.Count);

        var (named, _) = ModeLabDescriber.ByRules("Phrygian at 140 bpm, a new melody", Current, newSeed: 7);
        Assert.Equal((ScaleMode.Phrygian, 140.0, 7), (named.Mode, named.Bpm, named.Seed));

        Assert.Empty(ModeLabDescriber.ByRules("like a sunset by the sea", Current, newSeed: 7).Changes);

        // A model that restates the current tempo and purity has changed nothing.
        var (_, restated) = ModeLabDescriber.Apply("""{"bpm":100,"purity":90}""", Current);
        Assert.Empty(restated);
        var (fromModel, moved) = ModeLabDescriber.Apply("""{"mode":"Dorian","bpm":80}""", Current);
        Assert.Equal((ScaleMode.Dorian, 80.0), (fromModel.Mode, fromModel.Bpm));
        Assert.Equal(2, moved.Count);
    }
}
