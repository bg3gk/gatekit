using System.Text.Json.Nodes;
using GateKit.Symbols.Decomp;
using GateKit.Symbols.Registry;

namespace GateKit.Symbols.UnitTests;

/// <summary>Our reading of a function (`decomp code`): what is certain is written into the code,
/// what is supposed beside it. The pseudo-code here is invented for the tests.</summary>
public sealed class ReadingTests
{
    static JsonObject Json(string text) => (JsonObject)JsonNode.Parse(text)!;

    /// <summary>A registry of toy entries: one id a function start, the entries by id.</summary>
    static ReadingKnowledge Knowing(Dictionary<long, string[]> at, Dictionary<string, JsonObject> entries, string build = "100")
    {
        var k = new ReadingKnowledge("Toy.exe", build, id => entries.GetValueOrDefault(id));
        foreach (var (rva, ids) in at)
            foreach (var id in ids)
                k.Place(rva, new KnownId(id, entries.GetValueOrDefault(id)?["rung"]?.GetValue<string>() ?? "unknown", false));
        return k;
    }

    const string Caller = """
        void FUN_140001000(longlong param_1,int param_2)

        {
          int iVar1;

          iVar1 = FUN_140002000(param_1);
          FUN_140003000(param_2);
          FUN_140004000();
          return;
        }
        """;

    [Fact]
    public void A_function_on_an_applied_rung_is_written_into_the_code_and_one_on_a_shown_rung_beside_it()
    {
        var k = Knowing(new() { [0x2000] = ["toy::Draw"], [0x3000] = ["toy::Guessed"] }, new()
        {
            ["toy::Draw"] = Json("""{"id": "toy::Draw", "rung": "confirmed"}"""),
            ["toy::Guessed"] = Json("""{"id": "toy::Guessed", "rung": "inferred"}"""),
        });
        var reading = OurReading.Make(Caller, 0x1000, k, "void FUN_140001000(longlong param_1,int param_2)");
        Assert.Contains("  iVar1 = toy::Draw(param_1);\n", reading.Text);
        Assert.Contains("  FUN_140003000(param_2);   // FUN_140003000: toy::Guessed [inferred]\n", reading.Text);
        Assert.Contains("  FUN_140004000();\n", reading.Text);                  // nothing known there
        Assert.DoesNotContain("toy::Guessed(", reading.Text);                   // a guess never reads as code
        var applied = reading.Notes.Single(n => n.Id == "toy::Draw");
        Assert.Equal((6, "function", "FUN_140002000", true), (applied.Line, applied.Kind, applied.Machine, applied.Applied));
        Assert.False(reading.Notes.Single(n => n.Id == "toy::Guessed").Applied);
    }

    [Fact]
    public void Folded_ids_at_one_address_are_all_shown()
    {
        var k = Knowing(new() { [0x2000] = ["toy::Draw", "toy::DrawAgain"], [0x1000] = ["toy::A", "toy::B"] }, new()
        {
            ["toy::Draw"] = Json("""{"id": "toy::Draw", "rung": "confirmed"}"""),
            ["toy::DrawAgain"] = Json("""{"id": "toy::DrawAgain", "rung": "guess", "guess": {"rests_on": "r", "would_confirm": "w"}}"""),
            ["toy::A"] = Json("""{"id": "toy::A", "rung": "inferred", "does": "Does a thing."}"""),
            ["toy::B"] = Json("""{"id": "toy::B", "rung": "unknown"}"""),
        });
        var reading = OurReading.Make(Caller, 0x1000, k, null);
        Assert.Contains("iVar1 = toy::Draw(param_1);   // toy::Draw: the same code as toy::DrawAgain [guess]", reading.Text);
        Assert.Equal(["toy::A", "toy::B"], reading.Ids.Select(i => i.Id));
        Assert.Equal("// 2 ids share this code (the linker folded them)", reading.Header[0]);
        Assert.Contains(reading.Header, l => l.StartsWith("// toy::A  [inferred: shown", StringComparison.Ordinal));
        Assert.Contains(reading.Header, l => l.StartsWith("// toy::B  [unknown: shown", StringComparison.Ordinal));
    }

    [Fact]
    public void A_live_guess_beside_a_committed_id_is_a_clash_until_it_is_settled()
    {
        var entries = new Dictionary<string, JsonObject>
        {
            ["toy::Draw"] = Json("""{"id": "toy::Draw", "rung": "inferred"}"""),
            ["toy::Paint"] = Json("""{"id": "toy::Paint", "rung": "guess", "guess": {"rests_on": "It is called once a frame.", "would_confirm": "A watch on the frame count."}}"""),
        };
        (string? Keep, bool Folded)? settled = null;
        var k = new ReadingKnowledge("Toy.exe", "100", id => entries.GetValueOrDefault(id), _ => settled);
        foreach (var at in new long[] { 0x1000, 0x2000 })
        {
            k.Place(at, new KnownId("toy::Draw", "inferred", false));
            k.Place(at, new KnownId("toy::Paint", "guess", true));
        }
        var reading = OurReading.Make(Caller, 0x1000, k, null);
        Assert.Equal("// 2 ids claimed for this code: a clash, not settled - `gatekit registry settle Toy.exe+0x1000 --folded` if the linker folded them, --keep <id> if one is right",
            reading.Header[0]);
        Assert.Contains("//   the guess rests on: It is called once a frame.", reading.Header);
        Assert.Contains("//   it would be confirmed by: A watch on the frame count.", reading.Header);
        Assert.DoesNotContain(reading.Header, l => l.Contains(".;", StringComparison.Ordinal));
        Assert.Contains("also claimed, a clash: toy::Paint [guess]", reading.Text);

        settled = (null, true);
        Assert.Equal("// 2 ids share this code (the linker folded them)", OurReading.Make(Caller, 0x1000, k, null).Header[0]);
        settled = ("toy::Draw", false);
        Assert.Equal("// 2 ids claimed for this code; settled for toy::Draw", OurReading.Make(Caller, 0x1000, k, null).Header[0]);
    }

    [Fact]
    public void A_name_that_matches_one_function_in_each_binary_is_told_to_take_binary()
    {
        var two = new List<FunctionRow> { new(1, "bg3.exe", 0x10, "F", null, null), new(2, "bg3_dx11.exe", 0x20, "F", null, null) };
        Assert.Equal("2 functions match, one in each binary: add --binary bg3.exe or --binary bg3_dx11.exe to pick one, or --all to print them all",
            CodeCommand.Ambiguity(two));
        var three = two.Append(new FunctionRow(3, "bg3.exe", 0x30, "F2", null, null)).ToList();
        Assert.StartsWith("3 functions match; pick one by its binary+0xRVA", CodeCommand.Ambiguity(three), StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_says_what_it_does_its_rung_its_evidence_and_the_test_that_proves_it()
    {
        var k = Knowing(new() { [0x1000] = ["toy::Widget::Update"] }, new()
        {
            ["toy::Widget::Update"] = Json("""
                {"id": "toy::Widget::Update", "rung": "confirmed", "state": "described", "does": "Moves the widget one step.",
                 "evidence": [{"kind": "watch", "says": "a write watch on the widget's step caught it"}, {"kind": "test", "test": "toy/widget/step"}]}
                """),
        });
        var header = OurReading.Make(Caller, 0x1000, k, null).Header;
        Assert.Equal(
        [
            "// toy::Widget::Update  [confirmed: applied, written into the code]",
            "//   does: Moves the widget one step.",
            "//   evidence (watch): a write watch on the widget's step caught it",
            "//   proven by: test:toy/widget/step",
        ], header);
        Assert.Equal(["// no entry of the registry in build 100"], OurReading.Make(Caller, 0x9000, k, null).Header);
    }

    [Fact]
    public void A_variable_is_written_by_its_name_only_on_an_applied_rung_and_never_while_its_storage_is_unknown()
    {
        var k = Knowing(new() { [0x1000] = ["toy::F"] }, new()
        {
            ["toy::F"] = Json("""
                {"id": "toy::F", "rung": "inferred",
                 "parameters": [{"storage": "reg:rcx", "name": "widget", "rung": "confirmed"}, {"storage": "reg:rdx", "name": "count"}],
                 "locals": [{"storage": "unresolved", "decompiler_name": "iVar1", "name": "drawn", "rung": "confirmed"}]}
                """),
        });
        var reading = OurReading.Make(Caller, 0x1000, k, "void FUN_140001000(longlong param_1,int param_2)");
        Assert.Contains("void FUN_140001000(longlong widget,int param_2)   // param_2: count [inferred]", reading.Text);
        Assert.Contains("iVar1 = FUN_140002000(widget);", reading.Text);             // the confirmed parameter in every line
        Assert.Contains("  FUN_140003000(param_2);\n", reading.Text);                 // a shown name is said once, on its first line
        Assert.Contains("  int iVar1;   // iVar1: drawn [confirmed]", reading.Text);  // a temporary stays the machine's
        Assert.Equal([1, 6], reading.Notes.Where(n => n.Kind == "variable" && n.Applied).Select(n => n.Line));   // widget, in the signature and the call
    }

    const string Fields = """
        void FUN_140005000(longlong param_1,longlong *param_2)

        {
          uVar1 = *(ulonglong *)(param_1 + 0x70);
          Use((param_1 + 104));
          x = *(int *)(param_1 + 0x10);
          y = *(int *)(param_1 + 0x8);
          z = *(long *)(param_2 + 0x70);
          w = *(long *)(myparam_1 + 0x70);
          return;
        }
        """;

    static ReadingKnowledge FieldsKnown(string tieRung, string streamRung = "confirmed") => Knowing(new() { [0x5000] = ["toy::Read"] }, new()
    {
        ["toy::Read"] = Json($$"""
            {"id": "toy::Read", "rung": "confirmed",
             "parameters": [{"storage": "reg:rcx", "name": "stream"}, {"storage": "reg:rdx", "name": "other"}],
             "ties": [{"type": "toy::Stream", "via": "stream", "rung": "{{tieRung}}"}, {"type": "toy::Stream", "via": "other", "rung": "confirmed"}]}
            """),
        ["toy::Stream"] = Json($$"""
            {"id": "toy::Stream", "kind": "struct", "size": 128,
             "fields": [{"offset": 8, "name": "Count", "rung": "inferred"}, {"offset": 104, "name": "Seed", "rung": "game"}, {"offset": 112, "name": "NumRolls", "type": "uint64_t", "rung": "{{streamRung}}"}]}
            """),
    });

    [Fact]
    public void A_field_is_written_by_name_only_where_the_tie_and_the_field_are_applied_and_the_arithmetic_counts_bytes()
    {
        const string signature = "void FUN_140005000(longlong param_1,longlong *param_2)";
        var reading = OurReading.Make(Fields, 0x5000, FieldsKnown("confirmed"), signature);
        Assert.Contains("uVar1 = stream->NumRolls /*+0x70*/;", reading.Text);
        Assert.Contains("Use((&stream->Seed /*+0x68*/));", reading.Text);
        Assert.Contains("x = *(int *)(stream + 0x10);\n", reading.Text);                                 // no field at that offset
        Assert.Contains("y = *(int *)(stream + 0x8);   // stream + 0x8: toy::Stream.Count [inferred]", reading.Text);   // a shown field
        Assert.Contains("z = *(long *)(other + 0x70);\n", reading.Text);                                 // a typed pointer counts elements
        Assert.Contains("w = *(long *)(myparam_1 + 0x70);", reading.Text);                               // a longer name that ends the same

        // a tie on a shown rung: every field stays the machine's, the reading beside it
        var shownTie = OurReading.Make(Fields, 0x5000, FieldsKnown("inferred"), signature);
        Assert.Contains("uVar1 = *(ulonglong *)(stream + 0x70);   // stream + 0x70: toy::Stream.NumRolls [confirmed, tie inferred]", shownTie.Text);
        Assert.DoesNotContain("->", shownTie.Text);
        // a field on a shown rung under an applied tie
        Assert.Contains("// stream + 0x70: toy::Stream.NumRolls [extender]", OurReading.Make(Fields, 0x5000, FieldsKnown("confirmed", "extender"), signature).Text);
    }

    [Fact]
    public void The_legend_lists_the_structures_with_the_rung_of_each_field()
    {
        var legend = OurReading.Make(Fields, 0x5000, FieldsKnown("confirmed"), null).Legend;
        Assert.Equal(
        [
            "// stream points at toy::Stream  [tie confirmed, 0x80 bytes]",
            "//   +0x8  Count  [inferred]",
            "//   +0x68  Seed  [game]",
            "//   +0x70  NumRolls  uint64_t  [confirmed]",
        ], legend.Take(4));
        Assert.Equal("// other points at toy::Stream  [tie confirmed, 0x80 bytes]", legend[4]);
    }

    [Fact]
    public void A_live_claim_is_read_with_the_committed_entries_and_one_of_another_build_is_left_out()
    {
        var store = new RegistryStore(GateKit.Testing.Scratch.NewPath("reading-registry"));
        var claims = new List<Claim>
        {
            new(EntryKind.Function, Json("""{"id": "toy::Live", "rung": "confirmed"}"""), "Toy.exe", 0x2000, "100", null, "w", DateTime.UtcNow, "t", null),
            new(EntryKind.Function, Json("""{"id": "toy::Elsewhere", "rung": "confirmed"}"""), "Toy.exe", 0x3000, "99", null, "w", DateTime.UtcNow, "t", null),
            new(EntryKind.Function, Json("""{"id": "toy::OtherBinary", "rung": "confirmed"}"""), "Other.exe", 0x4000, "100", null, "w", DateTime.UtcNow, "t", null),
        };
        var k = ReadingKnowledge.Of(store, claims, "Toy.exe", "100");
        var reading = OurReading.Make(Caller, 0x1000, k, null);
        Assert.Contains("iVar1 = toy::Live(param_1);", reading.Text);
        Assert.Contains("  FUN_140003000(param_2);\n", reading.Text);
        Assert.Contains("  FUN_140004000();\n", reading.Text);
        Assert.True(k.At(0x2000).Single().Live);
    }

    [Fact]
    public void The_json_carries_the_raw_text_untouched_and_every_annotation()
    {
        var k = Knowing(new() { [0x2000] = ["toy::Draw"], [0x3000] = ["toy::Guessed"] }, new()
        {
            ["toy::Draw"] = Json("""{"id": "toy::Draw", "rung": "confirmed"}"""),
            ["toy::Guessed"] = Json("""{"id": "toy::Guessed", "rung": "inferred"}"""),
        });
        var json = OurReading.ToJson("Toy.exe", 0x1000, "100", Caller, OurReading.Make(Caller, 0x1000, k, null));
        Assert.Equal(Caller, json["raw"]!.GetValue<string>());
        var notes = json["annotations"]!.AsArray().Select(n => (n!["id"]!.GetValue<string>(), n["applied"]!.GetValue<bool>(), n["line"]!.GetValue<int>())).ToList();
        Assert.Equal([("toy::Draw", true, 6), ("toy::Guessed", false, 7)], notes);
        Assert.Equal("0x1000", json["rva"]!.GetValue<string>());
    }

    [Fact]
    public void A_callee_the_decompiler_named_is_found_by_that_name()
    {
        const string code = "void FUN_140001000(void)\n\n{\n  Toy_Thing_Do(1);\n  xToy_Thing_Do(2);\n  return;\n}";
        var k = Knowing(new() { [0x2000] = ["toy::Thing::Do"] }, new() { ["toy::Thing::Do"] = Json("""{"id": "toy::Thing::Do", "rung": "game"}""") });
        var text = OurReading.Make(code, 0x1000, k, null, new Dictionary<long, string> { [0x2000] = "Toy_Thing_Do" }).Text;
        Assert.Contains("  toy::Thing::Do(1);", text);
        Assert.Contains("  xToy_Thing_Do(2);", text);
    }
}
