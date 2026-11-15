using System.Text;
using System.Text.Json.Nodes;
using GateKit.Names;
using GateKit.Copies;

namespace GateKit.UnitTests;

public sealed class NamesTests : IDisposable
{
    readonly string record = GateKit.Testing.Scratch.NewPath("names");

    public NamesTests() => Directory.CreateDirectory(Path.Combine(record, "layouts", "alive"));

    public void Dispose()
    {
        if (Directory.Exists(record)) Directory.Delete(record, true);
    }

    string Layout(string component, string fields, string status = "confirmed", string more = "")
    {
        string path = Path.Combine(record, "layouts", "alive", component.Replace("::", ".") + ".json");
        File.WriteAllText(path, $$"""
            {
              "component": "{{component}}",
              "ext_name": "X",
              "size": 16,
              "status": "{{status}}",{{more}}
              "fields": [{{fields}}]
            }
            """.ReplaceLineEndings("\n"), new UTF8Encoding(false));
        return path;
    }

    static string Field(string name, int offset, string source = "unknown", string type = "uint32_t", string evidence = "seen") =>
        $$"""{"name": "{{name}}", "offset": {{offset}}, "type": "{{type}}", "source": "{{source}}", "evidence": "{{evidence}}"}""";

    static string RealRecord() => Path.Combine(CopySettings.RepositoryRoot()!, "research", "coverage");

    // ---- the measure

    [Theory]
    [InlineData("field_8", true)]
    [InlineData("Field_8", true)]      // whatever the case
    [InlineData("_Pad5", true)]
    [InlineData("unknownThing", true)]
    [InlineData("unk_3", true)]
    [InlineData("pad3", true)]
    [InlineData("dummy", true)]
    [InlineData("padding", false)]     // pad needs its digit
    [InlineData("Timer", false)]
    [InlineData("Fields", false)]
    public void A_machines_name_is_told_from_ours(string name, bool machine) => Assert.Equal(machine, Tally.IsMachine(name));

    [Fact]
    public void The_tally_counts_names_padding_and_sources()
    {
        Layout("esv::lock::A", string.Join(",", Field("Timer", 0, "inferred"), Field("field_4", 4), Field("_Pad8", 8, "inferred"), Field("Owner", 12, "game")));
        Layout("esv::lock::B", Field("field_0", 0));
        Layout("esv::lock::C", string.Join(",", Field("Speed", 0, "extender"), Field("Kept", 4)));
        Layout("ecl::Hypothesis", Field("field_0", 0), status: "hypothesis");          // not in the build
        Layout("ecl::Held", Field("field_0", 0), more: "\n  \"hold\": \"waits\",");    // held back from C++, still written down
        Layout("ecl::System", Field("field_0", 0), more: "\n  \"kind\": \"system\",");
        Layout("ecl::Empty", "");

        var tally = Tally.Measure(LayoutStore.All(record));
        Assert.Equal(4, tally.Layouts);
        Assert.Equal(8, tally.Fields);
        Assert.Equal(4, tally.Named);
        Assert.Equal(2, tally.NamedWithEvidence);    // Timer (inferred), Owner (game)
        Assert.Equal(1, tally.ExtenderUnchecked);    // Speed: the extender's name, nobody checked it
        Assert.Equal(1, tally.NamedUnknown);         // Kept: a name at rung unknown
        Assert.Equal(4, tally.MachineNamed);
        Assert.Equal(1, tally.Padding);
        Assert.Equal(3, tally.Owed);
        Assert.Equal(1, tally.Facts);
        Assert.Equal(("unknown", 4), tally.BySource[0]);
        Assert.Equal([("game", 1, 0), ("toolkit", 0, 0), ("inferred", 1, 1), ("extender", 1, 0), ("unknown", 1, 3)], tally.ByRung);
        Assert.Equal(("esv::lock", 3, 7), tally.ByNamespace[0]);
        var json = tally.ToJson([]);
        Assert.Equal(4, (int)json["named"]!);                    // the old key keeps its meaning
        Assert.Equal(2, (int)json["named_with_evidence"]!);
        Assert.Equal(1, (int)json["by_rung"]!["extender"]!["named"]!);
        Assert.Equal(1, Tally.Measure(LayoutStore.All(record), "ecl::").Fields);
    }

    [Fact]
    public void The_real_record_adds_up()
    {
        var unreadable = new List<string>();
        var tally = Tally.Measure(LayoutStore.All(RealRecord(), unreadable.Add));
        Assert.Empty(unreadable);
        Assert.True(tally.Fields > 0);
        Assert.Equal(tally.Fields, tally.Named + tally.MachineNamed);
        Assert.Equal(tally.Named, tally.NamedWithEvidence + tally.ExtenderUnchecked + tally.NamedUnknown);
        Assert.Equal(tally.Fields, tally.ByRung.Sum(r => r.Named + r.Machine));
        Assert.Equal(tally.Fields, tally.BySource.Sum(s => s.Fields));
        Assert.True(tally.Padding <= tally.MachineNamed);
    }

    // ---- the writer

    /// <summary>Naming one field must change that field's lines and nothing else, so the writer has
    /// to reproduce the shape every committed layout already has - all of them, to the byte.</summary>
    [Fact]
    public void Writing_a_layout_back_changes_nothing()
    {
        var changed = LayoutStore.All(RealRecord())
            .Where(f => !LayoutStore.Render(f).AsSpan().SequenceEqual(f.Bytes))
            .Select(f => f.Name).ToList();
        Assert.True(changed.Count == 0, $"{changed.Count} layouts would be reformatted, e.g. {string.Join(", ", changed.Take(5))}");
    }

    [Fact]
    public void A_name_is_written_with_its_rung_and_evidence()
    {
        string path = Layout("esv::lock::A", string.Join(",", Field("field_0", 0), Field("field_4", 4)));
        var written = FieldNames.Apply(record, [new NameChange("esv::lock::A", 4, "Timer", "inferred", "counts down in UpdateLocks")]);
        Assert.Equal("field_4", Assert.Single(written).Was);
        var field = JsonNode.Parse(File.ReadAllText(path))!["fields"]![1]!;
        Assert.Equal("Timer", (string?)field["name"]);
        Assert.Equal("inferred", (string?)field["source"]);
        Assert.Equal("counts down in UpdateLocks", (string?)field["evidence"]);
        Assert.Null(field["legacy"]);                       // field_4 is not an alias worth keeping
        Assert.DoesNotContain("\r", File.ReadAllText(path));
    }

    [Fact]
    public void The_games_name_wins_and_the_old_one_stays_as_an_alias()
    {
        string path = Layout("esv::lock::A", Field("Timer", 0, "inferred"));
        FieldNames.Apply(record, [new NameChange("A", 0, "Cooldown", "game", "ls::FieldMeta<float esv::lock::A::*, 0>")]);
        var field = JsonNode.Parse(File.ReadAllText(path))!["fields"]![0]!;
        Assert.Equal("Cooldown", (string?)field["name"]);
        Assert.Equal("Timer", (string?)field["legacy"]);

        var refused = Assert.Throws<NamesException>(() => FieldNames.Apply(record, [new NameChange("A", 0, "Guess", "inferred", "looks like it")]));
        Assert.Contains("does not replace it", refused.Message);
        FieldNames.Apply(record, [new NameChange("A", 0, "Guess", "inferred", "looks like it")], force: true);
    }

    [Fact]
    public void An_extenders_name_gives_way_to_one_with_evidence_and_not_the_other_way()
    {
        string path = Layout("esv::lock::A", Field("Speed", 0, "unknown"));
        FieldNames.Apply(record, [new NameChange("A", 0, "Speed", "extender", "Script Extender declares esv::lock::A::Speed at +0x0")]);
        var field = JsonNode.Parse(File.ReadAllText(path))!["fields"]![0]!;
        Assert.Equal("extender", (string?)field["source"]);
        Assert.Null(field["legacy"]);                                  // the same name: no alias

        FieldNames.Apply(record, [new NameChange("A", 0, "Velocity", "inferred", "UpdateLocks divides +0 by the frame time")]);
        field = JsonNode.Parse(File.ReadAllText(path))!["fields"]![0]!;
        Assert.Equal("Velocity", (string?)field["name"]);
        Assert.Equal("Speed", (string?)field["legacy"]);               // the extender's name stays as an alias
        var refused = Assert.Throws<NamesException>(() => FieldNames.Apply(record, [new NameChange("A", 0, "Speed", "extender", "x")]));
        Assert.Contains("does not replace it", refused.Message);
    }

    [Fact]
    public void Inferred_names_that_cite_only_the_extender_are_listed_not_demoted()
    {
        string path = Layout("esv::lock::A", string.Join(",",
            Field("Phase", 0, "inferred", evidence: "SE ecl::EffectHandlerPlayTime::Phase"),
            Field("Time", 4, "inferred", evidence: "SE names it Time; bg3.exe+0x18F2470 writes 1.0"),
            Field("Other", 8, "inferred", evidence: "counts down in UpdateLocks")));
        var before = File.ReadAllBytes(path);
        var listed = FieldNames.InferredWithoutCode(LayoutStore.All(record));
        Assert.Equal(["Phase"], listed.Select(n => n.Name));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Candidates_go_in_only_when_they_hold_together()
    {
        Layout("esv::lock::Had", Field("field_0", 0));
        string candidates = Path.Combine(record, "candidates");
        Directory.CreateDirectory(candidates);
        void Candidate(string component, string fields, int size = 8, bool named = true) =>
            File.WriteAllText(Path.Combine(candidates, component.Replace("::", ".") + ".json"),
                $$"""{"component": "{{component}}", {{(named ? "\"ext_name\": \"X\", " : "")}}"size": {{size}}, "status": "declared", "fields": [{{fields}}]}""");
        Candidate("esv::lock::Good", string.Join(",", Field("Speed", 0, "extender"), Field("field_4", 4)));
        Candidate("esv::lock::Had", Field("Speed", 0, "extender"));
        Candidate("esv::lock::Gap", Field("Late", 4, "extender"));
        Candidate("esv::lock::Short", Field("Speed", 0, "extender"), size: 16);
        Candidate("esv::lock::Unranked", string.Join(",", Field("Speed", 0, "unknown"), Field("Late", 4, "extender")));
        Candidate("esv::lock::Nameless", string.Join(",", Field("Speed", 0, "extender"), Field("Late", 4, "extender")), named: false);
        File.WriteAllText(Path.Combine(record, "coverage.json"), """{"components": [{"name": "esv::lock::Registered", "totalSize": 16}]}""");
        Candidate("esv::lock::Registered", string.Join(",", Field("Speed", 0, "extender"), Field("Late", 4, "extender")));

        var dry = Intake.Take(record, candidates, "extender", null, write: false);
        Assert.False(Directory.Exists(Path.Combine(record, "layouts", "extender")));
        var result = Intake.Take(record, candidates, "extender", null, write: true);
        Assert.Equal(dry.Taken, result.Taken);
        Assert.Equal(["esv::lock::Good"], result.Taken);
        Assert.Equal(["esv::lock::Had"], result.AlreadyThere);
        string Why(string component) => result.Refused.Single(r => r.Component == component).Reason;
        Assert.Contains("add the unknown field in between", Why("esv::lock::Gap"));
        Assert.Contains("expected 16", Why("esv::lock::Short"));
        Assert.Contains("rung unknown, not extender", Why("esv::lock::Unranked"));
        Assert.Contains("no ext_name", Why("esv::lock::Nameless"));
        Assert.Contains("the game registers 16", Why("esv::lock::Registered"));
        var file = LayoutStore.Read(Path.Combine(record, "layouts", "extender", "esv.lock.Good.json"))!;
        Assert.Equal(LayoutStore.Serialize(file.Json, true), file.Bytes);                  // in the record's own shape
        Assert.Contains("esv::lock::Good", Intake.Take(record, candidates, "extender", null, write: true).AlreadyThere);   // never taken twice
    }

    [Fact]
    public void The_capture_compares_every_layout_taken_from_the_extender()
    {
        var batch = LayoutStore.All(RealRecord()).Where(f => f.Batch == "extender").ToList();
        Assert.NotEmpty(batch);
        var loaded = GateKit.Mcp.LayoutCheck.Load(RealRecord()).Select(l => l.Component).ToHashSet(StringComparer.Ordinal);
        Assert.Empty(batch.Where(f => !loaded.Contains(f.Component)).Select(f => f.Component));
    }

    [Theory]
    [InlineData(0, "Timer", "inferred", "", "evidence")]                  // no evidence
    [InlineData(0, "Timer", "plausible", "x", "source must be")]          // not a rung
    [InlineData(0, "field_9", "inferred", "x", "machine's name")]         // a machine's name is not a name
    [InlineData(0, "has space", "inferred", "x", "not a name")]
    [InlineData(2, "Timer", "inferred", "x", "no field starts there")]    // naming is not layout work
    [InlineData(0, "Other", "inferred", "x", "already called")]           // taken
    [InlineData(8, "Timer", "inferred", "x", "is padding")]
    public void A_name_that_cannot_be_backed_is_refused(int offset, string name, string source, string evidence, string says)
    {
        string path = Layout("esv::lock::A", string.Join(",", Field("field_0", 0), Field("Other", 4, "inferred"), Field("_Pad8", 8)));
        var before = File.ReadAllBytes(path);
        var refused = Assert.Throws<NamesException>(() => FieldNames.Apply(record, [new NameChange("esv::lock::A", offset, name, source, evidence)]));
        Assert.Contains(says, refused.Message);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_batch_is_written_whole_or_not_at_all()
    {
        string path = Layout("esv::lock::A", string.Join(",", Field("field_0", 0), Field("field_4", 4)));
        var before = File.ReadAllBytes(path);
        Assert.Throws<NamesException>(() => FieldNames.Apply(record,
        [
            new NameChange("esv::lock::A", 0, "Timer", "inferred", "x"),
            new NameChange("esv::lock::A", 4, "Owner", "inferred", ""),
        ]));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(2, FieldNames.Apply(record,
        [
            new NameChange("esv::lock::A", 0, "Timer", "inferred", "x"),
            new NameChange("esv::lock::A", 4, "Owner", "inferred", "y"),
        ]).Count);
    }

    [Fact]
    public void A_layout_somebody_else_changed_meanwhile_is_not_overwritten()
    {
        string path = Layout("esv::lock::A", Field("field_0", 0));
        var file = LayoutStore.Read(path)!;
        FieldNames.Set(file, new NameChange("esv::lock::A", 0, "Timer", "inferred", "x"));
        File.AppendAllText(path, "\n");
        Assert.Throws<NamesException>(() => LayoutStore.Replace(file));
        Assert.DoesNotContain("Timer", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
    }

    [Fact]
    public void A_key_nobody_knows_is_refused_rather_than_ignored()
    {
        var refused = Assert.Throws<NamesException>(() => FieldNames.Parse(JsonNode.Parse(
            """{"component": "a::B", "offset": "0x18", "name": "Timer", "source": "inferred", "evidence": "x", "offsett": 3}""")));
        Assert.Contains("offsett", refused.Message);
        var one = Assert.Single(FieldNames.Parse(JsonNode.Parse(
            """[{"component": "a::B", "offset": "0x18", "name": "Timer", "source": "inferred", "evidence": "x"}]""")));
        Assert.Equal(24, one.Offset);
    }

    [Fact]
    public void A_runs_claim_waits_until_the_field_is_named()
    {
        Layout("esv::lock::A", string.Join(",", Field("field_0", 0), Field("Timer", 4, "inferred")));
        File.WriteAllLines(Path.Combine(record, "findings.jsonl"),
        [
            """{"component":"esv::lock::A","evidence":"value forced","reached_by":"play","recipe":"r.lua","fields":{"0x0":"the picker","0x4":"a timer"}}""",
            """{"component":"esv::lock::A","evidence":"sighting only","reached_by":"play","fields":{"0x0":"maybe"}}""",
            // the enum value the findings ledger takes now: a sighting is never offered either
            """{"component":"esv::lock::A","evidence":"sighting","reached_by":"play","fields":{"0x0":"perhaps"}}""",
            """{"component":"esv::lock::Unwritten","evidence":"repeated","reached_by":"cheat","fields":{"8":"a flag"}}""",
        ]);
        var pending = FieldNames.Pending(record);
        Assert.Equal(2, pending.Count);
        Assert.Equal(("esv::lock::A", "0x0", "still field_0"), (pending[0].Component, pending[0].Offset, pending[0].State));
        Assert.Contains("no layout", pending[1].State);
    }

    // ---- the declarations

    [Fact]
    public void A_comment_is_wrapped_at_spaces_and_hyphens()
    {
        Assert.Equal(["inferred: a", "countdown in", "seconds"], TextWrap.Wrap("inferred: a countdown in seconds", 12));
        Assert.Equal(["turn-", "based"], TextWrap.Wrap("turn-based", 6));
        Assert.Equal(["abcdef", "gh"], TextWrap.Wrap("abcdefgh", 6));
        Assert.Empty(TextWrap.Wrap("   ", 10));
    }

    [Fact]
    public void Declarations_are_rendered_from_the_layouts_in_the_build()
    {
        string tree = Path.Combine(record, "fork");
        string definitions = Path.Combine(tree, "BG3Extender", "GameDefinitions");
        Directory.CreateDirectory(Path.Combine(definitions, "Components"));
        Directory.CreateDirectory(Path.Combine(definitions, "Enumerations"));
        File.WriteAllText(Path.Combine(definitions, "Components", "Other.h"),
            "BEGIN_NS(esv)\nstruct Theirs : public BaseComponent\n{\n    DEFINE_COMPONENT(Theirs, \"esv::lock::Theirs\")\n};\nEND_NS()\n");
        File.WriteAllText(Path.Combine(definitions, "Enumerations", "ECS.inl"),
            "BEGIN_ENUM(ExtComponentType, uint32_t)\r\n    E(Theirs)\r\n    E(Max)\r\nEND_ENUM()\r\n");

        JsonObject Make(string component, string ext, string status, params string[] fields) => JsonNode.Parse($$"""
            {"component": "{{component}}", "ext_name": "{{ext}}", "size": 16, "status": "{{status}}", "fields": [{{string.Join(",", fields)}}]}
            """)!.AsObject();
        var ours = Make("esv::lock::Ours", "ServerLockOurs", "confirmed",
            Field("Timer", 0, "inferred", "float", "counts down in UpdateLocks"),
            """{"name": "Owner", "offset": 8, "type": "EntityHandle", "source": "game", "evidence": "FieldMeta", "legacy": "field_8"}""");
        var theirs = Make("esv::lock::Theirs", "Theirs", "confirmed", Field("A", 0, "inferred", "uint64_t"), Field("B", 8, "inferred", "uint64_t"));
        var guess = Make("esv::lock::Guess", "Guess", "hypothesis", Field("A", 0, "inferred", "uint64_t"));
        var files = new[] { ours, theirs, guess }.Select((json, i) => new LayoutFile($"{i}.json", json, [])).ToList();

        var declarations = new Declarations(tree, null);
        var result = declarations.Run(files);
        Assert.Equal(1, result.Emitted);
        Assert.Equal(["esv::lock::Theirs"], result.DeclaredUpstream);       // the author's declaration wins
        Assert.Equal(0, result.InvalidInBuild);
        Assert.Single(result.Invalid);                                      // the hypothesis does not add up, and may not
        string header = result.HeaderText!;
        Assert.DoesNotContain("\r", header);
        Assert.Contains("BEGIN_NS(esv::lock)", header);
        Assert.Contains("    DEFINE_COMPONENT(ServerLockOurs, \"esv::lock::Ours\")", header);
        Assert.Contains("    // inferred: counts down in UpdateLocks\n    float Timer;", header);
        Assert.Contains("    // the game's own member name: FieldMeta\n    [[bg3::legacy(field_8)]] EntityHandle Owner;", header);
        Assert.Contains("static_assert(sizeof(bg3se::esv::lock::Ours) == 16);", header);
        Assert.DoesNotContain("Guess", header);

        string enumeration = result.EnumerationText!;
        Assert.Contains("    E(ServerLockOurs)\r\n    // Coverage project: end\r\n\r\n    E(Max)\r\n", enumeration);
        File.WriteAllText(declarations.Enumerations, enumeration);
        Assert.Equal(enumeration, declarations.Run(files).EnumerationText);  // a second run finds its own block and replaces it
    }

    [Fact]
    public void A_layout_in_the_build_that_does_not_add_up_stops_the_run()
    {
        string tree = Path.Combine(record, "fork");
        Directory.CreateDirectory(Path.Combine(tree, "BG3Extender", "GameDefinitions"));
        var layout = JsonNode.Parse($$"""
            {"component": "esv::lock::Bad", "ext_name": "Bad", "size": 16, "status": "confirmed",
             "fields": [{{Field("A", 0, "inferred", "uint8_t")}}, {{Field("B", 2, "inferred", "uint32_t")}}]}
            """)!.AsObject();
        var result = new Declarations(tree, null).Run([new LayoutFile("bad.json", layout, [])]);
        Assert.Equal(1, result.InvalidInBuild);
        Assert.Contains("esv::lock::Bad.B: offset 2 is not aligned to 4", Assert.Single(result.Invalid));
        Assert.Null(result.HeaderText);
    }

    [Fact]
    public void Names_go_into_the_checkout_one_stands_in()
    {
        // A role in a worktree of its own ran the keeper's binary and wrote into the keeper's record.
        string checkout = Path.Combine(record, "checkout");
        Directory.CreateDirectory(Path.Combine(checkout, "research", "coverage", "layouts"));
        Directory.CreateDirectory(Path.Combine(checkout, "src", "deep"));
        File.WriteAllText(Path.Combine(checkout, "GateKit.slnx"), "<Solution />");
        Assert.Equal(checkout, NamesPaths.Here(Path.Combine(checkout, "src", "deep")));
        Assert.Null(NamesPaths.Here(record));
    }

    [Fact]
    public void The_fork_tree_is_found_inside_the_folder_that_holds_its_worktrees()
    {
        // se-fork is a container of worktrees (bg3se-v34, bg3se-v34-coverage, ...), not a tree itself.
        string container = Path.Combine(record, "se-fork");
        string Tree(string name)
        {
            string path = Path.Combine(container, name);
            Directory.CreateDirectory(Path.Combine(path, "BG3Extender", "GameDefinitions"));
            return path;
        }
        Directory.CreateDirectory(Path.Combine(container, "builds"));

        string only = Tree("bg3se-v34");
        Assert.Equal(only, NamesPaths.ForkTree(only));       // a tree is taken as it is
        Assert.Equal(only, NamesPaths.ForkTree(container));  // one tree inside: that one

        Tree("bg3se-v34-coverage");
        var several = Assert.Throws<NamesException>(() => NamesPaths.ForkTree(container));
        Assert.Contains("bg3se-v34, bg3se-v34-coverage", several.Message);
        Assert.Contains("--tree", several.Message);

        var none = Assert.Throws<NamesException>(() => NamesPaths.ForkTree(Path.Combine(container, "builds")));
        Assert.Contains("not a Script Extender source tree", none.Message);
    }

    [Fact]
    public void The_command_has_the_subcommands_the_prompts_name()
    {
        var command = NamesCommands.CreateCommand();
        Assert.Equal("names", command.Name);
        Assert.Equal(["adopt", "demand", "extender", "generate", "pending", "set", "suggest", "take", "tally", "worst"],command.Subcommands.Select(c => c.Name).Order());
    }
}
