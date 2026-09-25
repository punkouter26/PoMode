using PoMode.Shared.Analysis;
using Xunit;

namespace PoMode.Unit.SongStatistics;

/// <summary>Reading a model's reply as it streams, and catching the figures it made up.</summary>
public class InterpretationReplyTests
{
    [Fact]
    public void A_reply_streamed_one_character_at_a_time_reads_back_whole()
    {
        // A code fence the model wrapped it in, escapes split across chunks, and a scalar field first.
        const string reply = "```json\n{\"inData\": false, \"answer\": \"Line one.\\n\\nIt says \\\"caf\\u00e9\\\".\"}\n```";
        var reader = new ReplyReader();
        var streamed = "";

        foreach (var character in reply)
        {
            foreach (var (field, text) in reader.Push(character.ToString()))
            {
                Assert.Equal("answer", field);
                streamed += text;
            }
        }

        Assert.Equal("Line one.\n\nIt says \"café\".", reader["answer"]);
        Assert.Equal(reader["answer"], streamed);
        Assert.Equal("false", reader["inData"]);
        Assert.Null(reader["theory"]);
    }

    [Fact]
    public void Only_figures_the_measurements_do_not_contain_are_flagged()
    {
        const string shown = "- Tempo: 96 BPM\n- Notes inside the key: 63.4%\n- Average length 0.48s\n- median A3";

        var unsupported = GroundingCheck.Unsupported(
            "At 96 BPM, 63% of notes sit in the key, each about 0.5s long, around A3 — the 7th rings, "
            + "and 65% of the song is in 1987 style at 1.6 beats a second.",
            shown);

        // Quoting at a coarser precision is quoting; small counts and note names are how theory talks.
        Assert.Equal(["65", "1987", "1.6"], unsupported);
    }
}
