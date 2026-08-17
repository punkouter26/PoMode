using System.Text.Json;

namespace PoMode.TestCommon;

/// <summary>
/// Loads the shared Basic Pitch decoder fixture (spec §5: "the decoder algorithm is specified once
/// and implemented twice"). The C# unit test and the Playwright JS-port test both assert against
/// this one file, so a divergence between the two decoders fails a test that names the note.
/// </summary>
public sealed class BasicPitchFixture
{
    public const string FileName = "basic-pitch-decoder.fixture.json";

    public required double FramesPerSecond { get; init; }
    public required int MinMidi { get; init; }
    public required float[][] Onsets { get; init; }
    public required float[][] Frames { get; init; }
    public required ExpectedNote[] ExpectedNotes { get; init; }

    public sealed record ExpectedNote(int MidiPitch, double StartSec, double DurationSec, int Velocity);

    /// <summary>The raw fixture JSON, for handing to a browser test verbatim.</summary>
    public static string ReadJson()
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, FileName));

    public static BasicPitchFixture Load()
        => JsonSerializer.Deserialize<BasicPitchFixture>(ReadJson(), new JsonSerializerOptions(JsonSerializerDefaults.Web))
           ?? throw new InvalidOperationException($"{FileName} deserialized to null.");

    /// <summary>The decoder takes rectangular arrays; JSON can only hold jagged ones.</summary>
    public static float[,] ToRectangular(float[][] jagged)
    {
        var result = new float[jagged.Length, jagged[0].Length];
        for (var r = 0; r < jagged.Length; r++)
        {
            for (var c = 0; c < jagged[r].Length; c++)
            {
                result[r, c] = jagged[r][c];
            }
        }
        return result;
    }
}
