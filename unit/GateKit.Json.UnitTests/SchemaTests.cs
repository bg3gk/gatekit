using System.Text.Json.Nodes;

namespace GateKit.Json.UnitTests;

/// <summary>The schemas: the subset, the registry, the rules fixed in them, on unlike kinds - a flat
/// ledger row, a nested lead with references, a settings document.</summary>
public class SchemaTests
{
    static SchemaSet Set => SchemaSet.Embedded;

    [Fact]
    public void Every_schema_of_the_repository_is_within_the_subset_and_the_registry_resolves()
    {
        // Loading checks every schema against the subset and every binding against a schema.
        var folder = SchemaSet.FromFolder(Repo.Schemas);
        Assert.True(folder.All.Count >= 17, $"{folder.All.Count} schemas");
        Assert.Equal(folder.All.Select(s => s.Id).Order(), Set.All.Select(s => s.Id).Order());
        foreach (var file in Directory.EnumerateFiles(Repo.Schemas, "*.schema.json", SearchOption.AllDirectories))
        {
            var kind = Path.GetRelativePath(Repo.Schemas, file).Replace('\\', '/')[..^".schema.json".Length];
            Assert.Equal(kind, folder.Get(kind).Kind);
        }
    }

    [Fact]
    public void A_keyword_outside_the_subset_is_refused_with_its_line()
    {
        using var temp = new TempFolder();
        temp.Write("schemas/index.json", """{"schema": "registry/index/1", "files": [], "tools": []}""");
        temp.Write("schemas/x/thing.schema.json", "{\n  \"$id\": \"x/thing/1\",\n  \"type\": \"object\",\n  \"minProperties\": 1\n}\n");
        var e = Assert.Throws<JsonProblemException>(() => SchemaSet.FromFolder(temp.File("schemas")));
        var p = Assert.Single(e.Problems);
        Assert.Equal(4, p.Line);
        Assert.Contains("minProperties", p.What);
    }

    [Fact]
    public void Properties_without_a_stated_additionalProperties_and_a_broken_reference_are_refused()
    {
        using var temp = new TempFolder();
        temp.Write("schemas/index.json", """{"schema": "registry/index/1", "files": [{"path": "*.json", "kind": "x/none/1"}], "tools": []}""");
        temp.Write("schemas/x/thing.schema.json", """{"$id": "x/thing/1", "type": "object", "properties": {"a": {"$ref": "#/$defs/missing"}}}""");
        var e = Assert.Throws<JsonProblemException>(() => SchemaSet.FromFolder(temp.File("schemas")));
        Assert.Contains(e.Problems, p => p.What.Contains("additionalProperties"));
        Assert.Contains(e.Problems, p => p.What.Contains("names nothing"));
        Assert.Contains(e.Problems, p => p.What.Contains("no schema of kind x/none"));
    }

    static JsonObject Sighting() => new()
    {
        ["type"] = "eoc::camp::SettingsComponent", ["side"] = "client", ["time"] = "2026-11-14T09:14:50Z", ["instance"] = "world",
        ["role"] = "sweep", ["run"] = "sweep-trial", ["stretch"] = "boosts", ["level"] = "TUT_Avernus_C", ["label"] = "sweep:boosts:arm",
        ["source"] = "tick", ["state"] = "repeated", ["capture"] = "world@2026-11-14T09:14:41Z#2c608d",
    };

    [Fact]
    public void A_ledger_row_fits_and_each_rule_names_its_field()
    {
        Assert.Empty(Set.Validate(Sighting(), "record/sightings"));
        var row = Sighting();
        row["side"] = "both";
        row["time"] = "2026-11-14 09:14";
        row["level"] = "";
        row.Remove("run");
        row["extra"] = 1;
        var misfits = Set.Validate(row, "record/sightings/1");
        Assert.Contains(misfits, m => m.Rule == "enum" && m.Pointer == "/side" && m.What.Contains("client, server"));
        Assert.Contains(misfits, m => m.Rule == "format" && m.Pointer == "/time" && m.What.Contains("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Contains(misfits, m => m.Rule == "minLength" && m.Pointer == "/level");
        Assert.Contains(misfits, m => m.Rule == "required" && m.What.Contains("`run`"));
        Assert.Contains(misfits, m => m.Rule == "additional" && m.Pointer == "/extra");
    }

    [Fact]
    public void A_nested_document_with_references_is_checked_to_the_bottom_and_placed_on_its_line()
    {
        var text = """
            {"type": "ecl::FadeComponent", "side": "both", "one_frame": false, "class": "gameplay_state", "reachable": "solo",
             "creators": [{"system": "ecl::FadeSystem", "function": "bg3_dx11.exe+0x18E28C0", "binary": "bg3_dx11.exe",
                           "site": null, "depth": 2, "confidence": "medium"}],
             "entry": {"kind": "system_update", "detail": "ecl::FadeSystem", "basis": "calls"},
             "data": [], "how": "Play.", "instrument": "teleport", "score": 48, "state": "open",
             "attempts": [{"time": "2026-11-13T19:10", "run": "r", "instance": "i", "instrument": "play", "outcome": "empty", "note": "n", "source": "report"}]}
            """;
        var parsed = JsonText.Parse(text, "leads.jsonl");
        var problems = Set.Validate(parsed, "record/lead");
        Assert.Contains(problems, p => p.Rule == "null" && p.Pointer == "/creators/0/site" && p.Line == 3);
        Assert.Contains(problems, p => p.Rule == "enum" && p.Pointer == "/instrument" && p.Line == 5);
        Assert.Contains(problems, p => p.Rule == "format" && p.Pointer == "/attempts/0/time" && p.Line == 6);
        Assert.Equal(3, problems.Count);
    }

    [Fact]
    public void An_empty_array_becomes_an_object_where_the_schema_says_object_and_the_reverse()
    {
        var counts = new JsonObject { ["time"] = "2026-11-14T08:01:11Z", ["run"] = "r", ["definition"] = "counts-2", ["fields_named_by_rung"] = new JsonArray() };
        Assert.Empty(Set.Validate(counts, "record/counts"));
        Assert.IsType<JsonObject>(counts["fields_named_by_rung"]);

        var lead = JsonText.Parse("""{"hand": {}}""", "x").Root.AsObject();
        Set.Validate(lead, "record/lead");
        Assert.IsType<JsonArray>(lead["hand"]);

        // only empty ones: a full array where an object belongs is a misfit
        var wrong = new JsonObject { ["time"] = "2026-11-14T08:01:11Z", ["run"] = "r", ["definition"] = "counts-2", ["fields_named_by_rung"] = new JsonArray(1) };
        Assert.Contains(Set.Validate(wrong, "record/counts"), m => m.Rule == "type");
    }

    [Fact]
    public void A_document_names_its_kind_and_version()
    {
        var settings = new JsonObject { ["schema"] = "settings/gatekit/1", ["home"] = "/srv/gk", ["startTimeout"] = 75, ["packer"] = "builtin" };
        Assert.Empty(Set.Validate(settings, "settings/gatekit"));
        Assert.Equal("settings/gatekit/1", JsonText.ResolveKind("/elsewhere/mine.json", settings));
        settings["schema"] = "settings/gatekit/2";
        Assert.Contains(Set.Validate(settings, "settings/gatekit"), m => m.Rule == "const");
        Assert.False(Set.TryGet("settings/gatekit/2", out _, out var why));
        Assert.Contains("version 1", why);
    }

    [Fact]
    public void Formats_hold_64_bit_integers_from_Lua_and_the_one_spelling_of_time()
    {
        Assert.Null(SchemaSet.FormatProblem("uint64", "18446744073709551615"));
        Assert.NotNull(SchemaSet.FormatProblem("uint64", "18446744073709551616"));
        Assert.NotNull(SchemaSet.FormatProblem("uint64", "007"));
        Assert.NotNull(SchemaSet.FormatProblem("uint64", "1.0"));
        Assert.Null(SchemaSet.FormatProblem("int64", "-9223372036854775808"));
        Assert.NotNull(SchemaSet.FormatProblem("int64", "-0"));
        Assert.Null(SchemaSet.FormatProblem("time", "2026-11-14T09:14:50Z"));
        Assert.NotNull(SchemaSet.FormatProblem("time", "2026-11-14T09:14:50+00:00"));
        Assert.NotNull(SchemaSet.FormatProblem("time", "2026-11-14 09:14"));
    }

    [Fact]
    public void OneOf_picks_its_branch_by_the_discriminator()
    {
        using var temp = new TempFolder();
        temp.Write("schemas/index.json", """{"schema": "registry/index/1", "files": [], "tools": []}""");
        temp.Write("schemas/x/event.schema.json", """
            {"$id": "x/event/1", "oneOf": [
              {"type": "object", "required": ["kind", "hp"], "additionalProperties": false,
               "properties": {"kind": {"const": "hit"}, "hp": {"type": "integer"}}},
              {"type": "object", "required": ["kind"], "additionalProperties": false,
               "properties": {"kind": {"const": "miss"}, "why": {"type": "string"}}}]}
            """);
        var set = SchemaSet.FromFolder(temp.File("schemas"));
        Assert.Empty(set.Validate(JsonNode.Parse("""{"kind": "hit", "hp": 3}"""), "x/event"));
        Assert.Contains(set.Validate(JsonNode.Parse("""{"kind": "hit", "why": "x"}"""), "x/event"), m => m.Rule == "required");
        Assert.Contains(set.Validate(JsonNode.Parse("""{"kind": "graze"}"""), "x/event"), m => m.Rule == "oneOf" && m.Pointer == "/kind" && m.What.Contains("\"hit\", \"miss\""));
        // canonical order follows the branch chosen
        Assert.Equal("{\"kind\":\"miss\",\"why\":\"x\"}", JsonText.FormatRow(JsonNode.Parse("""{"why": "x", "kind": "miss"}""")!, "x/event", set));
    }

    [Fact]
    public void The_registry_binds_paths_by_their_trailing_segments_and_globs()
    {
        Assert.Equal("record/sightings/1", Set.ForPath("/a/b/research/coverage/record/sightings.jsonl")!.Schema.Id);
        Assert.Equal("record/sightings/1", Set.ForPath(@"C:\x\research\coverage\record\sightings.jsonl")!.Schema.Id);
        Assert.Equal("bridge/answer/1", Set.ForPath("/tmp/channel/res_server_4120-17-1.json")!.Schema.Id);
        Assert.Equal("settings/gatekit/1", Set.ForPath("/home/me/gatekit.json")!.Schema.Id);
        Assert.Null(Set.ForPath("/a/b/research/coverage/record/sightings.jsonl.bak"));
        Assert.Null(Set.ForPath("/a/xresearch/coverage/findings.jsonl"));
        Assert.Null(Set.ForTool("bg3_nothing"));
    }

    [Fact]
    public void The_bridge_answer_is_strict_and_any_result_is_allowed()
    {
        Assert.Empty(Set.Validate(JsonNode.Parse("""{"id": "4120-1-1", "side": "server", "ok": true, "result": [null, {"a": []}], "log": []}"""), "bridge/answer"));
        Assert.Contains(Set.Validate(JsonNode.Parse("""{"id": "x", "ok": true, "res": 1}"""), "bridge/answer"), m => m.Rule == "additional");
    }

    [Fact]
    public void The_attention_fragment_holds_what_AttentionReader_keeps()
    {
        var items = JsonNode.Parse("""[{"seq": 4, "what": "dialogue", "side": "server", "since_ms": 120, "speaker": {"name": "Lae'zel"}}, {"waiting": true, "side": "client", "what": "roll"}]""");
        Assert.Empty(Set.Validate(items, "common/attention"));
        Assert.Contains(Set.Validate(JsonNode.Parse("""[{"side": "client", "waiting": false}]"""), "common/attention"), m => m.Rule == "const");
        var pages = JsonNode.Parse("""
            [{"waiting": true, "side": "client", "what": "setup", "window": "Eula", "firstLaunch": true,
              "controls": [{"handle": "AcceptBtn", "kind": "button", "command": "CustomEvent", "parameter": "OpenSensitiveSettings"}]},
             {"waiting": true, "side": "client", "what": "splash", "window": "SplashScreen", "controls": [], "keys": "any key"}]
            """);
        Assert.Empty(Set.Validate(pages, "common/attention"));
    }
}
