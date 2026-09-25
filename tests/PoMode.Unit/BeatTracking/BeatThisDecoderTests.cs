using PoMode.API.Features.BeatTracking;
using PoMode.API.Features.ModalAnalysis;
using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.BeatTracking;

public class BeatThisDecoderTests
{
    [Fact]
    public void Heard_downbeats_become_the_bars_and_number_the_measures()
    {
        // 120 BPM (25 frames a beat at 50 fps), bar one at 0.4 s, five bars — with the kind of mess
        // the model really emits: a flat-topped beat peak two frames wide, one beat missed outright,
        // and downbeat peaks a frame off the beat they belong to.
        const int FirstFrame = 20;
        var beatLogits = Enumerable.Repeat(-5f, 600).ToArray();
        var downbeatLogits = Enumerable.Repeat(-5f, 600).ToArray();
        for (var beat = 0; beat < 20; beat++)
        {
            if (beat != 6)
            {
                beatLogits[FirstFrame + (beat * 25)] = 3f;
            }
            if (beat % 4 == 0)
            {
                downbeatLogits[FirstFrame + (beat * 25) + 1] = 2f;
            }
        }
        beatLogits[FirstFrame + 51] = 3f; // beat 2's plateau: frames 70 and 71

        var (beats, downbeats) = BeatThisDecoder.Decode(beatLogits, downbeatLogits);
        var result = BeatThisDecoder.Summarize(beats, downbeats, "test");

        Assert.Equal(19, beats.Length);
        Assert.Equal(1.41, beats[2], precision: 3); // the plateau merged to its mean frame, 70.5
        Assert.Equal([0.4, 2.4, 4.4, 6.4, 8.4], downbeats); // snapped onto the beats
        Assert.Equal(120.0, result.Grid.Bpm, precision: 1);
        Assert.Equal(0.4, result.Grid.FirstBeatSec);
        Assert.Equal(downbeats, result.Grid.Downbeats);

        // The bar with the missed beat still reads 120: tempo is judged against the usual four beats
        // a bar, not the three that bar happened to contain.
        Assert.Equal(4, result.TempoMap.Measures.Count);
        Assert.All(result.TempoMap.Measures, measure => Assert.Equal(120.0, measure.Bpm));
        Assert.True(result.TempoMap.IsSteady);

        // Measure numbers follow the heard bars, not 4/4 from t=0: at 2.2 s the second bar has not
        // started yet (it starts at 2.4), though a grid laid from zero would already call it bar 2.
        ChordSpan[] chords = [new("C", "C", "maj", 0.0, 2.2), new("G", "G", "maj", 2.2, 4.5), new("F", "F", "maj", 4.5, 6.0)];
        var numbered = ModalAnalysisEngine.Analyze([], chords, result.Grid.Bpm, downbeats: result.Grid.Downbeats);
        var assumed = ModalAnalysisEngine.Analyze([], chords, result.Grid.Bpm);
        Assert.Equal([1, 1, 3], numbered.Windows.Select(w => w.MeasureNumber));
        Assert.Equal([1, 2, 3], assumed.Windows.Select(w => w.MeasureNumber));
    }
}
