using System.Text.Json.Nodes;
using GateKit.Copies;

namespace GateKit.UnitTests;

/// <summary>`gatekit timeline` (StartTimeline): the kinds it asks for, the chunk it sends, and one
/// line a row.</summary>
public sealed class StartTimelineTests
{
    [Fact]
    public void The_kinds_are_words_and_nothing_else_reaches_the_chunk()
    {
        Assert.Null(StartTimeline.Kinds(null));
        Assert.Null(StartTimeline.Kinds(" "));
        Assert.Equal(["State", "UIState"], StartTimeline.Kinds("State, UIState,"));
        var refused = Assert.Throws<CopyException>(() => StartTimeline.Kinds("State,\"] = os.exit() --"));
        Assert.Contains("is not a kind of row", refused.Message);
        Assert.StartsWith("local KINDS = nil\n", StartTimeline.Chunk(null));
        Assert.StartsWith("local KINDS = { [\"state\"] = true, [\"hintshown\"] = true }\n", StartTimeline.Chunk(["State", "HintShown"]));
    }

    [Fact]
    public void A_row_is_one_line_its_fields_in_a_fixed_order_the_counters_when_asked()
    {
        var read = JsonNode.Parse("""
            { "rows": [
                { "n": 405, "Ms": 10427.04, "Side": "Client", "What": "State", "To": "StartLoading", "From": "InitMenu",
                  "IoReadBytes": 312, "IoReadOps": 5943, "CpuMs": 7922.5, "FileOpens": 1223 },
                { "n": 407, "Ms": 10428.3325, "Side": "Client", "What": "HintsComposed", "Name": "2 mods' files", "GameCount": 49, "Count": 51 }
              ], "total": 593, "dropped": 0 }
            """)!.AsObject();
        var lines = StartTimeline.Lines(read, counters: false).ToList();
        Assert.Equal("  405    10427.0  Client    State              From=InitMenu To=StartLoading", lines[0]);
        Assert.Equal("  407    10428.3  Client    HintsComposed      Name=\"2 mods' files\" Count=51 GameCount=49", lines[1]);
        Assert.Equal("2 rows of 593", lines[2]);
        var counted = StartTimeline.Lines(read, counters: true).First();
        Assert.EndsWith("To=StartLoading IoReadBytes=312 IoReadOps=5943 CpuMs=7922.5 FileOpens=1223", counted);
    }

    [Fact]
    public void A_full_record_says_how_many_rows_it_dropped()
    {
        var read = JsonNode.Parse("""{ "rows": [], "total": 8192, "dropped": 17 }""")!.AsObject();
        Assert.Equal(["0 rows of 8192; the record was full and dropped 17 later rows"], StartTimeline.Lines(read, false));
    }
}
