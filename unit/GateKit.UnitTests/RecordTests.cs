using System.CommandLine;
using System.Text.Json.Nodes;
using GateKit.Mcp;
using GateKit.Copies;
using Record = GateKit.Copies.Record;

namespace GateKit.UnitTests;

/// <summary>
/// The ledgers of the coverage record (research/coverage/record): rows held to their vocabulary,
/// one id scheme for wishes, sightings that say who saw what when, and the three questions asked of
/// the whole record. Two unattended runs could not be queried from the record they left: per type
/// only a maximum and some labels, no time, copy, role or run; evidence in twelve free-text forms.
/// </summary>
public sealed class RecordTests : IDisposable
{
    readonly TempFolder _temp = new();

    public void Dispose()
    {
        RecordBook.Context.Reset();
        _temp.Dispose();
    }

    /// <summary>A checkout in miniature: two types, a layout, a system, recipes.</summary>
    string Checkout()
    {
        _temp.Write("GateKit.slnx", "<Solution />");
        _temp.Write("research/coverage/coverage.json", """
            {"components": [{"name": "esv::a::BComponent", "side": "server", "mapped": true},
                            {"name": "esv::a::CComponent", "side": "server", "mapped": true, "verified": {"date": "x"}}]}
            """);
        _temp.Write("research/coverage/seen/index.json", """{"esv::a::CComponent": {"server": {"max": 1, "labels": [], "levels": [], "samples": 0}}}""");
        _temp.Write("research/coverage/layouts/a/C.json", """
            {"component": "esv::a::CComponent", "status": "confirmed", "size": 8,
             "fields": [{"name": "Timer", "offset": 0, "type": "float", "source": "game", "evidence": "x"},
                        {"name": "field_4", "offset": 4, "type": "uint32_t"}]}
            """);
        _temp.Write("research/coverage/systems/esv__a/BSystem.json", """
            {"system": "esv::a::BSystem", "sure": "yes for the creation",
             "does": [{"method": "Update", "text": "it creates B", "check": {"when": "b_appears", "expect": [{"appears": {"component": "esv::a::BComponent"}}]}},
                      {"method": "Update", "text": "it removes B", "check": {"when": "no_such_recipe", "expect": [{"gone": {"component": "esv::a::BComponent"}}]}},
                      {"method": "Other", "text": "unreachable", "check": "not checkable live: inside one frame"}]}
            """);
        _temp.Write("tests/_coverage/b_appears.lua", "test { level = 'A:B' }\nfunction run(game) end\n");
        _temp.Write("tests/_coverage/c_appears.lua", "test { level = 'A:B' }\nfunction run(game) end\n");
        _temp.Write("research/coverage/findings.jsonl",
            """{"component":"esv::a::BComponent","recipe":"tests/_coverage/b_appears.lua","evidence":"repeated","reached_by":"play","kind":"finding"}""" + "\n"
            + """{"component":"esv::a::BComponent","recipe":"tests/_coverage/gone.lua","evidence":"sighting only","reached_by":"play","kind":"lead"}""" + "\n"
            + """{"component":"esv::a::BComponent","recipe":null,"moved_to":"research/coverage/leads.md","evidence":"sighting","reached_by":"cheat","kind":"lead"}""" + "\n");
        return _temp["research/coverage"];
    }

    [Fact]
    public void A_row_outside_its_ledger_s_vocabulary_is_refused_and_nothing_is_written()
    {
        var root = Checkout();
        var refused = Assert.Throws<ArgumentException>(() => Record.Append(root, "findings", new JsonObject
        {
            ["component"] = "esv::a::BComponent", ["evidence"] = "sighting only", ["reached_by"] = "play", ["kind"] = "lead",
        }));
        Assert.Contains("`evidence` is \"sighting only\"; it is one of sighting, repeated, replayed, field_value", refused.Message);
        Assert.Contains("nothing was written", refused.Message);
        Assert.Throws<ArgumentException>(() => Record.Append(root, "wishes", new JsonObject
            { ["id"] = "W-0001", ["time"] = "t", ["from"] = "x", ["text"] = "y", ["state"] = "done" }));
        Assert.Throws<ArgumentException>(() => Record.Append(root, "sightings", new JsonObject { ["type"] = "esv::a::BComponent" }));
        Assert.False(File.Exists(Record.PathOf(root, "wishes")));

        // A batch is all or nothing, and every line ends in LF alone (the record is committed).
        var good = new JsonObject { ["recipe"] = "tests/_coverage/b_appears.lua", ["time"] = "t", ["instance"] = "a", ["by"] = "r", ["ok"] = true };
        Assert.Throws<ArgumentException>(() => Record.Append(root, "replays", [good, new JsonObject { ["recipe"] = "x", ["ok"] = "yes" }]));
        Assert.False(File.Exists(Record.PathOf(root, "replays")));
        Record.Append(root, "replays", [good, (JsonObject)good.DeepClone()]);
        var text = File.ReadAllText(Record.PathOf(root, "replays"));
        Assert.DoesNotContain("\r", text);
        Assert.Equal(2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Wishes_have_one_id_scheme_and_change_state_by_appending()
    {
        var root = Checkout();
        var first = RecordBook.AddWish(root, "runner world", "GK.Act.Equip answers ok and equips nothing", was: "D6");
        var second = RecordBook.AddWish(root, "keeper", "bg3_up waits 300 s for a level it cannot reach", "sent");
        Assert.Equal("W-0001", (string)first["id"]!);
        Assert.Equal("W-0002", (string)second["id"]!);

        RecordBook.UpdateWish(root, "W-0001", "fixed", commit: "abc1234", note: "Equip refuses now");
        var rows = Record.Read(root, "wishes");
        Assert.Equal(3, rows.Count);   // nothing rewritten
        var now = RecordBook.Wishes(root);
        Assert.Equal(["W-0001", "W-0002"], now.Select(w => (string)w["id"]!));
        Assert.Equal("fixed", (string)now[0]["state"]!);
        Assert.Equal("abc1234", (string)now[0]["commit"]!);
        Assert.Equal("D6", (string)now[0]["was"]!);
        Assert.Equal("GK.Act.Equip answers ok and equips nothing", (string)now[0]["text"]!);
        Assert.Throws<ArgumentException>(() => RecordBook.UpdateWish(root, "W-0009", "fixed"));
        Assert.Throws<ArgumentException>(() => RecordBook.UpdateWish(root, "W-0002", "done"));
    }

    [Fact]
    public void A_sighting_says_who_saw_it_and_a_repeat_is_written_once_per_run_in_a_later_capture()
    {
        var root = Checkout();
        RecordBook.Context.Start("world", "runner world", "night-3", "2");
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "seen", "index.json")))!.AsObject();
        JsonArray Rows(params string[] names) => [.. names.Select(n => (JsonNode)new JsonObject { ["Name"] = n, ["Label"] = "lock", ["side"] = "server" })];

        // bg3_seen_new: B is new to the record, C was known before (the index has it).
        RecordBook.FromNew(root, "world", Rows("esv::a::BComponent"), Rows("esv::a::CComponent"), index);
        var rows = Record.Read(root, "sightings");
        Assert.Equal(2, rows.Count);
        var sighted = rows.Single(r => (string)r["type"]! == "esv::a::BComponent");
        Assert.Equal("sighted", (string)sighted["state"]!);
        Assert.Equal(("world", "runner world", "night-3", "2", "tick", "lock"),
            ((string)sighted["instance"]!, (string)sighted["role"]!, (string)sighted["run"]!, (string)sighted["stretch"]!,
             (string)sighted["source"]!, (string)sighted["label"]!));
        Assert.EndsWith("Z", (string)sighted["time"]!);
        Assert.Equal("repeated", (string)rows.Single(r => (string)r["type"]! == "esv::a::CComponent")["state"]!);

        // The drain of the same capture adds nothing: neither type is new, nor seen in a later capture.
        var types = JsonNode.Parse("""{"esv::a::BComponent": {"Max": 1, "Label": "lock"}, "esv::a::CComponent": {"Max": 2, "Label": "idle"}}""");
        var known = RecordBook.SeenKeys(JsonNode.Parse("""{"esv::a::BComponent": {"server": {}}, "esv::a::CComponent": {"server": {}}}""")!.AsObject());
        using (Record.Lock(root)) RecordBook.FromTypes(root, "world", "server", types, known, "TUT_Avernus_C");
        Assert.Equal(2, Record.Read(root, "sightings").Count);

        // A later capture of the same run confirms B once; C was already repeated in this run.
        RecordBook.Context.Start("world", null, null, null);
        using (Record.Lock(root)) RecordBook.FromTypes(root, "world", "server", types, known, "TUT_Avernus_C");
        rows = Record.Read(root, "sightings");
        Assert.Equal(3, rows.Count);
        Assert.Equal(("esv::a::BComponent", "repeated", "TUT_Avernus_C", "runner world"),
            ((string)rows[2]["type"]!, (string)rows[2]["state"]!, (string)rows[2]["level"]!, (string)rows[2]["role"]!));
        RecordBook.Context.Start("world", null, null, null);
        using (Record.Lock(root)) RecordBook.FromTypes(root, "world", "server", types, known, "TUT_Avernus_C");
        Assert.Equal(3, Record.Read(root, "sightings").Count);

        // The run's yield: first sightings by copy, role and hour.
        var yielded = RecordBook.Yield(root, "night-3");
        Assert.Equal(1, (int)yielded["first_seen"]!);
        Assert.Equal(2, (int)yielded["repeated"]!);
        Assert.Equal(1, (int)yielded["first_seen_by_role"]!["runner world"]!);
        Assert.Equal(1, (int)yielded["first_seen_by_instance"]!["world"]!);
        Assert.Null(yielded["replays_pass_share"]);
    }

    [Fact]
    public void Role_run_and_stretch_come_from_what_was_said_then_the_environment_then_unknown()
    {
        Assert.Equal(("pathfinder", "r", "s"), Record.Who("pathfinder", "r", "s"));
        var (role, _, _) = Record.Who(null, "r", "s");
        Assert.Equal(Environment.GetEnvironmentVariable("GATEKIT_ROLE") is { Length: > 0 } set ? set : "unknown", role);
    }

    [Fact]
    public void The_check_names_whatever_does_not_hold_together()
    {
        var root = Checkout();
        Record.Append(root, "sightings", new JsonObject
        {
            ["type"] = "esv::a::NobodyComponent", ["side"] = "server", ["time"] = "t", ["instance"] = "i", ["role"] = "r",
            ["run"] = "x", ["stretch"] = "s", ["source"] = "tick", ["state"] = "sighted",
        });
        File.AppendAllText(Path.Combine(root, "record", "sightings.jsonl"), "{ not json\n");
        var problems = RecordBook.Check(root);
        Assert.Contains(problems, p => p.Contains("findings.jsonl row 2: `evidence` is \"sighting only\""));
        Assert.Contains(problems, p => p.Contains("findings.jsonl row 2: tests/_coverage/gone.lua does not exist"));
        Assert.Contains(problems, p => p.Contains("c_appears.lua: replayable, and no line of findings.jsonl names it"));
        Assert.Contains(problems, p => p.Contains("BSystem.json claim 2: check.when no_such_recipe is no recipe"));
        Assert.Contains(problems, p => p.Contains("sightings.jsonl:2: not a JSON object"));
        Assert.Contains(problems, p => p.Contains("esv::a::NobodyComponent is not a registered type"));
        Assert.DoesNotContain(problems, p => p.Contains("row 3"));          // moved to the leads: named, not missing
        Assert.DoesNotContain(problems, p => p.Contains("b_appears"));      // a finding names it, and it exists
        Assert.Equal(6, problems.Count);

        // `gatekit record check` says the same and fails.
        var output = new StringWriter();
        var command = new RootCommand { RecordCommands.CreateCommand() };
        var code = command.Parse(["record", "check", "--coverage", root]).Invoke(new InvocationConfiguration { Output = output, Error = output });
        Assert.Equal(1, code);
        Assert.Contains("6 problems", output.ToString());
    }

    [Fact]
    public void The_tally_counts_with_the_names_tally_and_the_systems_tally_and_is_kept_with_write()
    {
        var root = Checkout();
        // names at the rungs the headline keeps apart: the extender's unchecked, and unknown
        _temp.Write("research/coverage/layouts/a/D.json", """
            {"component": "esv::a::DComponent", "status": "confirmed", "size": 8,
             "fields": [{"name": "Owner", "offset": 0, "type": "uint32_t", "source": "extender"},
                        {"name": "Flags", "offset": 4, "type": "uint32_t", "source": "unknown"}]}
            """);
        var row = RecordBook.Tally(root, "night-3");
        Assert.Equal("counts-2", (string)row["definition"]!);
        Assert.Equal(RecordBook.CountsDefinition, (string)row["definition"]!);
        Assert.Equal((2, 1, 1, 1), ((int)row["types"]!, (int)row["seen"]!, (int)row["never_seen"]!, (int)row["verified"]!));
        Assert.Equal(4, (int)row["fields"]!);
        // the headline of `gatekit names tally`: named with evidence only, the rest apart
        var names = GateKit.Names.Tally.Measure(GateKit.Names.LayoutStore.All(root, _ => { }));
        Assert.Equal(names.NamedWithEvidence, (int)row["fields_named"]!);
        Assert.Equal((1, 1, 1), ((int)row["fields_named"]!, (int)row["fields_extender_unchecked"]!, (int)row["fields_named_unknown"]!));
        Assert.Equal(1, (int)row["fields_named_by_rung"]!["game"]!);
        Assert.Equal(1, (int)row["fields_named_by_rung"]!["unknown"]!);
        Assert.Null(row["fields_named_by_rung"]!["toolkit"]);
        Assert.Equal(1, (int)row["owed"]!);
        Assert.Equal((1, 1, 3), ((int)row["systems_described"]!, (int)row["systems_sure"]!, (int)row["claims_written"]!));

        var output = new StringWriter();
        var command = new RootCommand { RecordCommands.CreateCommand() };
        Assert.Equal(0, command.Parse(["record", "tally", "--write", "--json", "--run", "night-3", "--coverage", root])
            .Invoke(new InvocationConfiguration { Output = output, Error = output }));
        var kept = Assert.Single(Record.Read(root, "counts"));
        Assert.Equal("night-3", (string)kept["run"]!);
        Assert.Contains("\"owed\": 1", output.ToString());
    }
}
