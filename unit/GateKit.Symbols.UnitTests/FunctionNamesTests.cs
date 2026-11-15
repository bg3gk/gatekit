using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>The ledger of functions named by evidence: what a name must carry, and that a row
/// written is the row read.</summary>
public sealed class FunctionNamesTests
{
    [Theory]
    [InlineData("FUN_14105aab0", "inferred", "a watch", "a machine's name")]
    [InlineData("sub_105AAB0", "inferred", "a watch", "a machine's name")]
    [InlineData("Draw Number", "inferred", "a watch", "one word")]
    [InlineData("", "inferred", "a watch", "one word")]
    [InlineData("esv::roll::Draw", "guessed", "a watch", "--source")]
    [InlineData("esv::roll::Draw", "inferred", "  ", "--evidence")]
    [InlineData("esv::roll::Draw", "toolkit", "", "--evidence")]
    public void A_name_without_meaning_source_or_evidence_is_refused(string name, string source, string evidence, string says)
    {
        Assert.Contains(says, FunctionNames.Problem(name, source, evidence));
    }

    [Fact]
    public void A_name_with_its_evidence_and_an_unknown_one_without_are_taken()
    {
        Assert.Null(FunctionNames.Problem("esv::roll::stream::DrawNumber", "inferred", "two hits of a memory watch on the stream's count"));
        Assert.Null(FunctionNames.Problem("Something", "unknown", ""));
    }

    [Fact]
    public void A_live_guess_on_a_named_function_is_a_row_of_its_own_a_clash_and_not_counted_as_named_by_evidence()
    {
        var folder = GateKit.Testing.Scratch.NewPath("names-live");
        try
        {
            var live = new GateKit.Symbols.Registry.LiveLayer(folder);
            var guess = new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "esv::roll::Paint", ["rung"] = "guess", ["does"] = "Paints the roll.",
                ["guess"] = new System.Text.Json.Nodes.JsonObject { ["rests_on"] = "it is called once a roll", ["would_confirm"] = "a watch on the roll's count" },
            };
            live.Note(new GateKit.Symbols.Registry.Claim(GateKit.Symbols.Registry.EntryKind.Function, guess, "bg3.exe", 0x10, "1", null, "w", DateTime.UtcNow, "t", null));
            var committed = new FunctionNames.Row("bg3.exe", 0x10, "esv::roll::Draw", "inferred", "a watch", "Draws a number.", []);
            var rows = FunctionNames.WithLive([committed], live);
            Assert.Equal(["esv::roll::Draw", "esv::roll::Paint"], rows.Select(r => r.Name));
            Assert.True(rows[1].Live);
            Assert.Contains("rests on: it is called once a roll", rows[1].Guess, StringComparison.Ordinal);
            Assert.Equal(IdStanding.Clash, FunctionNames.StandingOf(rows, live).Standing);
            Assert.Equal("1 name by evidence, 1 guess (1 function)", FunctionNames.Count(rows));

            live.Settle("bg3.exe", null, 0x10, null, true, "w", "t");
            Assert.Equal(IdStanding.Folded, FunctionNames.StandingOf(rows, live).Standing);
            Assert.Equal(IdStanding.One, FunctionNames.StandingOf([committed], live).Standing);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void A_structures_legend_lists_its_named_fields_by_offset()
    {
        var fields = new List<FunctionNames.Field>
        {
            new("esv::roll::stream::Stream", 0x70, "NumRolls", "uint64", "inferred", "a watch"),
            new("esv::roll::stream::Stream", 0x68, "Seed", null, "inferred", "a watch"),
            new("Other", 0x0, "First", null, "inferred", "a read"),
        };
        Assert.Equal(["esv::roll::stream::Stream", "  +0x68  Seed", "  +0x70  NumRolls  uint64"], FunctionNames.Legend(fields, "esv::roll::stream::Stream"));
        Assert.Contains("no field named yet", FunctionNames.Legend(fields, "Unknown").Single());
    }

    [Fact]
    public void A_row_written_is_the_row_read_in_the_order_of_binary_and_address()
    {
        var coverage = GateKit.Testing.Scratch.NewPath("functions");
        Directory.CreateDirectory(coverage);
        try
        {
            Assert.Empty(FunctionNames.Read(coverage));
            FunctionNames.WriteLedger(coverage,
            [
                new FunctionNames.Row("bg3_dx11.exe", 0x31C2C10, "esv::ApplyHealthChange", "inferred", "a watch on \"Hp\"", null, []),
                new FunctionNames.Row("bg3_dx11.exe", 0x105AAB0, "esv::roll::stream::DrawNumber", "inferred", "two hits", "Draws one number.", ["OldName"]),
            ]);
            var rows = FunctionNames.Read(coverage);
            Assert.Equal([0x105AAB0, 0x31C2C10], rows.Select(r => r.Rva));
            Assert.Equal(("esv::roll::stream::DrawNumber", "Draws one number.", "OldName"), (rows[0].Name, rows[0].Does, rows[0].Legacy.Single()));
            Assert.Equal("a watch on \"Hp\"", rows[1].Evidence);
            Assert.Equal("esv::ApplyHealthChange", FunctionNames.Find(coverage, "BG3_DX11.EXE", 0x31C2C10)!.Name);
            Assert.Null(FunctionNames.Find(coverage, "bg3_dx11.exe", 0x1));
            Assert.Null(FunctionNames.Find(null, "bg3_dx11.exe", 0x105AAB0));
        }
        finally { Directory.Delete(coverage, true); }
    }
}
