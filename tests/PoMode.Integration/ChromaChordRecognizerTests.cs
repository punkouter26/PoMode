using PoMode.API.Features.ChordRecognition;
using PoMode.API.Pipeline;
using PoMode.TestCommon;
using Xunit;

namespace PoMode.Integration;

public sealed class ChromaChordRecognizerTests : IDisposable
{
    private readonly string _jobDir = Path.Combine(Path.GetTempPath(), $"pomode-chords-{Guid.NewGuid():N}");

    public ChromaChordRecognizerTests() => Directory.CreateDirectory(_jobDir);

    public void Dispose() => Directory.Delete(_jobDir, recursive: true);

    private StageContext ContextWith(byte[] wav, string fileName = "instrumental.wav")
    {
        File.WriteAllBytes(Path.Combine(_jobDir, fileName), wav);
        return new StageContext("job1", _jobDir, Path.Combine(_jobDir, "input.wav"));
    }

    /// <summary>
    /// Builds one WAV containing two consecutive chords by summing each chord's tone run directly
    /// into a single sample buffer (rather than concatenating two independently-encoded WAVs and
    /// patching RIFF sizes, which is brittle for no benefit here).
    /// </summary>
    private static byte[] Progression(params (int Root, string Quality, double Seconds)[] chords)
    {
        const int sampleRate = 22050;
        var samples = new List<double>();
        foreach (var (root, quality, seconds) in chords)
        {
            var chordWav = TestAudio.MakeChord(seconds, TestAudio.Triad(root, quality), sampleRate);
            // MakeChord always writes a 44-byte PCM16 mono header at this sample rate; read the PCM
            // body back out and append it so every chord's run is a first-class series of samples.
            for (var i = 44; i + 1 < chordWav.Length; i += 2)
            {
                samples.Add(BitConverter.ToInt16(chordWav, i) / (double)short.MaxValue);
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var dataSize = samples.Count * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            writer.Write((short)(Math.Clamp(sample, -1.0, 1.0) * short.MaxValue));
        }
        writer.Flush();
        return stream.ToArray();
    }

    [Fact]
    public async Task Recognises_a_two_chord_progression()
    {
        var context = ContextWith(Progression((0, "maj", 2.0), (9, "min", 2.0))); // C then Am

        var spans = await new ChromaChordRecognizer().RecognizeAsync(context, CancellationToken.None);

        Assert.Equal(2, spans.Count);
        Assert.Equal("C", spans[0].Symbol);
        Assert.Equal("Am", spans[1].Symbol);
        Assert.InRange(spans[1].StartSec, 1.7, 2.3);
    }

    [Fact]
    public async Task Falls_back_to_the_job_input_when_there_is_no_instrumental_stem()
    {
        File.WriteAllBytes(Path.Combine(_jobDir, "input.wav"), TestAudio.MakeChord(3.0, TestAudio.Triad(7, "maj")));
        var context = new StageContext("job1", _jobDir, Path.Combine(_jobDir, "input.wav"));

        var spans = await new ChromaChordRecognizer().RecognizeAsync(context, CancellationToken.None);

        Assert.NotEmpty(spans);
        Assert.Equal("G", spans[0].Symbol);
    }

    [Fact]
    public async Task Silence_yields_no_chords_rather_than_throwing()
    {
        var context = ContextWith(TestAudio.MakeWav(seconds: 2.0));

        var spans = await new ChromaChordRecognizer().RecognizeAsync(context, CancellationToken.None);

        Assert.Empty(spans);
    }

    [Fact]
    public async Task It_is_always_available()
        => Assert.True(await new ChromaChordRecognizer().IsAvailableAsync(CancellationToken.None));
}
