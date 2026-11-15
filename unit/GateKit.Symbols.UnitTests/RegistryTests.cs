using System.Text.Json.Nodes;
using GateKit.Json;
using GateKit.Symbols.Decomp;
using GateKit.Symbols.Registry;

namespace GateKit.Symbols.UnitTests;

/// <summary>The registry: the import of the earlier stores and its round trip, one id one file, the
/// check's refusals, renaming, mending a table, the applied cut, and the live layer.</summary>
public sealed class RegistryTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("registry");
    readonly string coverage;
    readonly RegistryStore store;

    public RegistryTests()
    {
        coverage = Path.Combine(root, "coverage");
        Directory.CreateDirectory(Path.Combine(coverage, "layouts", "alive"));
        store = RegistryStore.Of(coverage);
        Directory.CreateDirectory(Path.Combine(store.Repository, "tests", "combat", "attack"));
        File.WriteAllText(Path.Combine(store.Repository, "tests", "combat", "attack", "ranged.lua"), "-- a test\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    static readonly string Watch = "a write watch on the stream's count caught it twice";

    static FunctionNames.Row Row(string name, long rva, IReadOnlyDictionary<string, string>? vars = null, IReadOnlyDictionary<string, string>? structs = null, string? does = "Draws one number from a stream.") =>
        new("game.exe", rva, name, "inferred", Watch, does, ["OurOldName"])
        {
            Vars = vars ?? new Dictionary<string, string>(), Structs = structs ?? new Dictionary<string, string>(),
            Build = "777", Fingerprint = new JsonObject { ["calls"] = new JsonArray("ns::Other") }, Size = 64, Body = new string('a', 64),
        };

    static string? Signature(string binary, long rva) => rva == 0x1000 ? "ulonglong FUN_141000(Stream *param_1, float param_2, int param_3, int param_4, int param_5)" : null;

    [Fact]
    public void The_earlier_stores_come_back_from_the_registry_unchanged_and_an_import_runs_again_without_harm()
    {
        var rows = new List<FunctionNames.Row>
        {
            Row("esv::roll::stream::DrawNumber", 0x1000,
                new SortedDictionary<string, string>(StringComparer.Ordinal) { ["param_1"] = "stream", ["param_2"] = "weight", ["param_5"] = "fifth", ["local_24c"] = "total", ["uVar3"] = "drawn" },
                new SortedDictionary<string, string>(StringComparer.Ordinal) { ["stream"] = "esv::roll::stream::Stream" }),
            Row("DecideAttackHit", 0x2000, does: null),
        };
        var fields = new List<FunctionNames.Field>
        {
            new("esv::roll::stream::Stream", 0x70, "NumRolls", "uint64", "inferred", Watch),
            new("esv::roll::stream::Stream", 0x68, "Seed", null, "extender", "Script Extender's State.Seed"),
        };
        File.WriteAllText(Path.Combine(coverage, "layouts", "alive", "eoc.HealthComponent.json"), """
            {
              "component": "eoc::HealthComponent",
              "ext_name": "Health",
              "oneFrame": false,
              "size": 8,
              "status": "confirmed",
              "summary": "",
              "fields": [
                {"name": "Hp", "offset": 0, "type": "int32_t", "source": "game", "evidence": "the macOS build's member name", "legacy": "field_0"},
                {"name": "field_4", "offset": 4, "type": "int32_t", "source": "unknown", "evidence": ""}
              ],
              "open_questions": ["what +4 counts"],
              "seen": true
            }
            """);
        var output = new StringWriter();
        var functions = RegistryImport.Functions(store, rows, Signature, _ => new string('b', 64), false, false, output);
        Assert.Equal((2, 2, 0, 0, 2), (functions.Read, functions.Written, functions.Unchanged, functions.Refused, functions.RoundTrip));
        Assert.Contains("OurOldName is our own and is not kept", output.ToString());
        var structs = RegistryImport.Structs(store, fields, false, false, output);
        Assert.Equal((2, 2, 2), (structs.Read, structs.Written, structs.RoundTrip));
        var layouts = RegistryImport.Layouts(store, coverage, false, false, output);
        Assert.Equal((1, 1, 1), (layouts.Read, layouts.Written, layouts.RoundTrip));

        // by storage, never by the decompiler's name; a temporary keeps that name until its storage is read
        var draw = store.Get("esv::roll::stream::DrawNumber")!.Json;
        var storages = (draw["parameters"] as JsonArray)!.Concat((JsonArray)draw["locals"]!).Select(v => ((string)v!["storage"]!, (string)v["name"]!)).ToList();
        Assert.Contains(("reg:rcx", "stream"), storages);
        Assert.Contains(("reg:xmm1", "weight"), storages);
        Assert.Contains(("stack:+0x28", "fifth"), storages);
        Assert.Contains(("stack:-0x24C", "total"), storages);
        Assert.Contains(("unresolved", "drawn"), storages);
        Assert.Null(draw["known_as"]);                                              // our own earlier name is not kept
        Assert.NotNull(store.Get("unplaced::DecideAttackHit"));                     // no owner known: unplaced
        Assert.Equal("game-name", (string?)store.Get("eoc::HealthComponent")!.Json["fields"]![0]!["evidence"]![0]!["kind"]);
        Assert.Equal("esv", Path.GetFileNameWithoutExtension(store.Shards("777", "game.exe")[0]));
        Assert.Equal(new string('b', 64), store.Info("777")!.Binaries["game.exe"]);

        // again: nothing written, everything equal
        var again = RegistryImport.Functions(store, rows, Signature, _ => null, false, false, TextWriter.Null);
        Assert.Equal((0, 2, 2), (again.Written, again.Unchanged, again.RoundTrip));
        Assert.Equal(1, RegistryImport.Layouts(store, coverage, false, false, TextWriter.Null).Unchanged);

        // the readers of the earlier stores now read the registry
        Assert.Equal(2, FunctionNames.Read(coverage, Signature).Count);
        Assert.Equal(2, FunctionNames.ReadFields(coverage).Count);
    }

    [Fact]
    public void The_committed_stores_of_the_repository_survive_the_import_whole()
    {
        var record = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "research", "coverage"));
        Assert.True(Directory.Exists(Path.Combine(record, "layouts")), record);
        var scratch = new RegistryStore(Path.Combine(root, "real"));
        var rows = FunctionNames.ReadLedger(record);
        var fields = FunctionNames.ReadFieldLedger(record);
        var f = RegistryImport.Functions(scratch, rows, (_, _) => null, _ => null, false, false, TextWriter.Null);
        var s = RegistryImport.Structs(scratch, fields, false, false, TextWriter.Null);
        var l = RegistryImport.Layouts(scratch, record, false, false, TextWriter.Null);
        Assert.Equal((rows.Count, 0, rows.Count), (f.Read, f.Refused, f.RoundTrip));
        Assert.Equal((fields.Count, 0, fields.Count), (s.Read, s.Refused, s.RoundTrip));
        Assert.Equal((l.Read, 0, l.Read), (l.Read, l.Refused, l.RoundTrip));
        Assert.Equal(Directory.EnumerateFiles(Path.Combine(record, "layouts"), "*.json", SearchOption.AllDirectories).Count(), l.Read);
    }

    JsonObject Function(string id, string does = "Rolls the dice of one request.") => new() { ["id"] = id, ["rung"] = "inferred", ["does"] = does };

    [Fact]
    public void One_id_has_one_file_and_two_ids_that_differ_by_case_are_refused()
    {
        store.Write(EntryKind.Function, Function("esv::RollSystem::RollDice"), create: true);
        Assert.Contains("one id, one file", Assert.Throws<RegistryException>(() => store.Write(EntryKind.Function, Function("esv::RollSystem::RollDice"), create: true)).Message);
        Assert.Contains("already a function", Assert.Throws<RegistryException>(() => store.Write(EntryKind.Type, new JsonObject { ["id"] = "esv::RollSystem::RollDice", ["kind"] = "struct" }, create: true)).Message);
        Assert.Contains("only by case", Assert.Throws<RegistryException>(() => store.Write(EntryKind.Function, Function("esv::RollSystem::ROLLDICE"), create: true)).Message);
    }

    [Theory]
    [InlineData("Rolled for the coordinator's run.", "provenance")]
    [InlineData("Seen on 2026-11-12 in a test.", "provenance")]
    [InlineData("Read at 14:05 in the game.", "provenance")]
    [InlineData("Opus thinks it rolls the dice.", "provenance")]
    [InlineData("Read from C:\\Users\\me\\dump.", "provenance")]
    [InlineData("It returns param_1 plus one.", "code")]
    [InlineData("It reads *(int *)(this + 8) and returns it.", "code")]
    [InlineData("   ", "sentence")]
    public void A_sentence_that_names_the_work_or_quotes_the_code_is_refused(string does, string rule)
    {
        store.Write(EntryKind.Function, Function("esv::A::B", does), create: false);
        var report = RegistryCheck.Run(store);
        Assert.Contains(report.Refusals, r => r.Rule == rule);
    }

    [Fact]
    public void The_check_refuses_an_applied_rung_without_confirming_evidence_and_owes_a_question_below_the_line()
    {
        var applied = Function("esv::A::Applied");
        applied["rung"] = "confirmed";
        applied["evidence"] = new JsonArray(new JsonObject { ["kind"] = "pairing", ["says"] = "paired with the Toolkit's twin" });
        store.Write(EntryKind.Function, applied, create: false);
        var guess = Function("esv::A::Guess");
        guess["rung"] = "guess";
        store.Write(EntryKind.Function, guess, create: false);
        var asked = Function("esv::A::Asked");
        asked["questions"] = new JsonArray(new JsonObject { ["ask"] = "Which action of a player makes this run?", ["about"] = "reach", ["state"] = "open" });
        store.Write(EntryKind.Function, asked, create: false);
        var report = RegistryCheck.Run(store);
        Assert.Contains(report.Refusals, r => r.Rule == "rung" && r.Where.Contains("Applied", StringComparison.Ordinal));
        Assert.Contains(report.Refusals, r => r.Rule == "rung" && r.Where.Contains("Guess", StringComparison.Ordinal));
        Assert.Contains(report.Owed, r => r.Rule == "questions" && r.Where.Contains("Guess", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Owed, r => r.Where.Contains("Asked", StringComparison.Ordinal));

        applied["evidence"]!.AsArray().Add(new JsonObject { ["kind"] = "watch", ["says"] = "the write of Hp came from it" });
        store.Write(EntryKind.Function, applied, create: false);
        Assert.DoesNotContain(RegistryCheck.Run(store).Refusals, r => r.Where.Contains("Applied", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_citation_is_resolved_and_one_that_leads_nowhere_is_refused_by_kind()
    {
        store.Write(EntryKind.Function, Function("esv::A::Known"), create: false);
        File.WriteAllText(Path.Combine(store.Repository, "notes.md"),
            "See code:esv::A::Known and code:esv::A::Gone.\nProven by test:combat/attack/ranged/crossbow/aim and test:combat/missing.\nlocal x = code:match(\"a\")\n");
        var report = RegistryCheck.Run(store);
        Assert.Contains(report.Refusals, r => r.Rule == "cite-code" && r.What.Contains("esv::A::Gone", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Refusals, r => r.What.Contains("esv::A::Known", StringComparison.Ordinal) || r.What.Contains("match", StringComparison.Ordinal));
        Assert.Contains(report.Refusals, r => r.Rule == "cite-test" && r.What.Contains("combat/missing", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Rule == "cite-test" && n.What.Contains("crossbow/aim", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rename_moves_the_file_and_rewrites_every_citation_and_leaves_the_old_id_nowhere()
    {
        store.Write(EntryKind.Function, Function("unplaced::Old"), create: false);
        var caller = Function("esv::A::Caller");
        caller["evidence"] = new JsonArray(new JsonObject { ["kind"] = "watch", ["concerns"] = "unplaced::Old", ["says"] = "it called code:unplaced::Old.total twice" });
        caller["fingerprint"] = new JsonObject { ["calls"] = new JsonArray("unplaced::Old") };
        store.Write(EntryKind.Function, caller, create: false);
        store.WriteTable("777", "game.exe", [new BuildRow("unplaced::Old", 0x10, 8, null)]);
        File.WriteAllText(Path.Combine(store.Repository, "notes.md"), "Rolls in code:unplaced::Old, then code:unplaced::Older.\n");

        RegistryRefs.Rename(store, "unplaced::Old", "esv::RollSystem::New", TextWriter.Null);

        Assert.Null(store.Get("unplaced::Old"));
        Assert.NotNull(store.Get("esv::RollSystem::New"));
        Assert.Equal("esv::RollSystem::New", Assert.Single(store.Table("777", "game.exe")).Id);
        Assert.Equal("Rolls in code:esv::RollSystem::New, then code:unplaced::Older.\n", File.ReadAllText(Path.Combine(store.Repository, "notes.md")));
        var after = store.Get("esv::A::Caller")!.Json;
        Assert.Equal("esv::RollSystem::New", (string?)after["evidence"]![0]!["concerns"]);
        Assert.Equal("esv::RollSystem::New", (string?)after["fingerprint"]!["calls"]![0]);
        Assert.DoesNotContain(RegistryRefs.All(store), c => RegistryRefs.Names(c.Address, "unplaced::Old"));
    }

    [Fact]
    public void A_table_a_merge_left_untidy_is_mended_and_two_addresses_for_one_id_are_refused()
    {
        store.Write(EntryKind.Function, Function("esv::A::One"), create: false);
        store.Write(EntryKind.Function, Function("esv::A::Two"), create: false);
        store.WriteTable("777", "game.exe", [new BuildRow("esv::A::One", 0x10, null, null)]);
        var shard = store.Shards("777", "game.exe").Single();
        File.WriteAllText(shard, "<<<<<<< HEAD\n{\"id\":\"esv::A::Two\",\"rva\":\"0x20\"}\n=======\n{\"id\":\"esv::A::One\",\"rva\":\"0x10\"}\n>>>>>>> other\n{\"id\":\"esv::A::One\",\"rva\":\"0x10\"}\n");
        Assert.Equal(2, store.Mend("777", "game.exe", TextWriter.Null));
        Assert.Equal(["esv::A::One", "esv::A::Two"], store.Table("777", "game.exe").Select(r => r.Id));
        File.AppendAllText(shard, "{\"id\":\"esv::A::One\",\"rva\":\"0x99\"}\n");
        Assert.Contains("different rows", Assert.Throws<RegistryException>(() => store.Mend("777", "game.exe", TextWriter.Null)).Message);
    }

    [Fact]
    public void Only_what_is_confirmed_is_in_the_applied_set()
    {
        var confirmed = Function("esv::A::Confirmed");
        confirmed["rung"] = "confirmed";
        confirmed["evidence"] = new JsonArray(new JsonObject { ["kind"] = "watch", ["says"] = "the write came from it" });
        store.Write(EntryKind.Function, confirmed, create: false);
        store.Write(EntryKind.Function, Function("esv::A::Inferred"), create: false);
        store.Write(EntryKind.Type, new JsonObject
        {
            ["id"] = "eoc::T", ["kind"] = "struct", ["size"] = 8,
            ["fields"] = new JsonArray(
                new JsonObject { ["offset"] = 0, ["name"] = "Game", ["rung"] = "game", ["evidence"] = new JsonArray(new JsonObject { ["kind"] = "game-name", ["says"] = "the macOS symbol" }) },
                new JsonObject { ["offset"] = 4, ["name"] = "Guessed", ["rung"] = "extender" }),
        }, create: false);
        store.WriteTable("777", "game.exe", [new BuildRow("esv::A::Confirmed", 0x10, null, null), new BuildRow("esv::A::Inferred", 0x20, null, null)]);
        var applied = Applied.Of(store, "game.exe", "777").ToList();
        Assert.Equal(["eoc::T", "esv::A::Confirmed"], applied.Select(a => (string)a["id"]!).Order(StringComparer.Ordinal));
        Assert.Equal("Game", (string?)Assert.Single((JsonArray)applied.First(a => (string?)a["id"] == "eoc::T")["fields"]!)!["name"]);
    }

    // --- the live layer ---

    LiveLayer Live() => new(Path.Combine(root, "home", "state", "registry-live"));

    static Claim Claim(string id, string? rung = "inferred", long? rva = 0x10, string who = "a", JsonObject? extra = null)
    {
        var entry = new JsonObject { ["id"] = id, ["does"] = "Rolls the dice of one request." };
        if (rung is not null) entry["rung"] = rung;
        foreach (var (k, v) in extra ?? []) entry[k] = v?.DeepClone();
        return new Claim(EntryKind.Function, entry, rva is null ? null : "game.exe", rva, "777", 8, who, DateTime.UtcNow, "task-1", null);
    }

    [Fact]
    public void A_guess_without_what_it_rests_on_is_refused()
    {
        Assert.Contains("rests on", Assert.Throws<RegistryException>(() => Live().Note(Claim("esv::A::G", "guess"))).Message);
        Live().Note(Claim("esv::A::G", "guess", extra: new JsonObject { ["guess"] = new JsonObject { ["rests_on"] = "it is called once a roll", ["would_confirm"] = "a watch on the stream's count" } }));
        Assert.Single(Live().Of("esv::A::G"));
    }

    [Fact]
    public void Writers_at_once_lose_nothing()
    {
        var live = Live();
        Parallel.For(0, 40, i => new LiveLayer(live.Folder).Note(Claim("esv::A::Shared", who: "w" + i)));
        Assert.Equal(40, live.Of("esv::A::Shared").Select(c => c.Who).Distinct().Count());
    }

    [Fact]
    public void A_commit_leaves_who_when_and_the_task_behind_and_a_clash_holds_only_itself()
    {
        var live = Live();
        live.Note(Claim("esv::A::Clean", rva: 0x10));
        live.Note(Claim("esv::A::First", rva: 0x20, who: "a"));
        live.Note(Claim("esv::A::Second", rva: 0x20, who: "b"));
        Assert.Single(live.Clashes(store));
        var done = live.Commit(store, TextWriter.Null);
        Assert.Equal(["esv::A::Clean"], done);
        var text = File.ReadAllText(store.PathOf(EntryKind.Function, "esv::A::Clean"));
        Assert.DoesNotContain("task-1", text);
        Assert.DoesNotContain("\"who\"", text);
        Assert.DoesNotContain("\"when\"", text);
        Assert.Equal(0x10, store.Table("777", "game.exe").Single().Rva);
        Assert.Null(store.Get("esv::A::First"));

        live.Settle("game.exe", "777", 0x20, "esv::A::Second", false, "a", "task-1");
        Assert.Empty(live.Clashes(store));
        Assert.Equal(["esv::A::Second"], live.Commit(store, TextWriter.Null));
        Assert.Empty(live.Commit(store, TextWriter.Null));          // what the checkout holds is not written again
    }

    [Fact]
    public void The_live_layer_keeps_its_files_where_the_contract_says()
    {
        // docs/contracts/cli.md, "The live layer": entries/<kind>/<scope>/<name>.json and settled/<binary>/<build or any>/<rva>.json
        var live = Live();
        live.Note(Claim("esv::RollSystem::RollDice"));
        live.Settle("game.exe", null, 0x10, "esv::RollSystem::RollDice", false, "a", "task-1");
        var file = Path.Combine(live.Folder, "entries", "functions", "esv", "RollSystem", "RollDice.json");
        Assert.True(File.Exists(file));
        Assert.Equal(LiveLayer.Schema, (string?)JsonNode.Parse(File.ReadAllText(file))!["schema"]);
        Assert.True(File.Exists(Path.Combine(live.Folder, "settled", "game%2Eexe", "any", "0x10.json")));
    }

    [Fact]
    public void A_withdrawn_claim_is_gone_for_every_reader_and_the_one_before_it_speaks_again()
    {
        var live = Live();
        var first = Claim("esv::A::Taken", who: "a") with { When = DateTime.UtcNow.AddSeconds(-2) };
        live.Note(first);
        var second = Claim("esv::A::Taken", rung: "guess", who: "b", extra: new JsonObject
        {
            ["guess"] = new JsonObject { ["rests_on"] = "it is called once a roll", ["would_confirm"] = "a watch on the stream's count" },
        });
        live.Note(second);
        var reader = new LiveLayer(live.Folder);   // another process's view of the same folder
        Assert.Equal("guess", reader.Latest().Single(c => c.Id == "esv::A::Taken").Rung);
        Assert.Single(live.At("game.exe", 0x10));

        var gone = Assert.Single(live.Withdraw("esv::A::Taken", all: false));
        Assert.Equal("b", gone.Who);
        Assert.Equal("inferred", reader.Latest().Single(c => c.Id == "esv::A::Taken").Rung);
        Assert.Empty(live.Withdraw("esv::A::Taken", all: true, who: "b"));   // that writer has none left

        Assert.Single(live.Withdraw("esv::A::Taken", all: true));
        Assert.Empty(reader.Of("esv::A::Taken"));
        Assert.Empty(reader.At("game.exe", 0x10));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(live.Folder, "entries"), "*.json", SearchOption.AllDirectories));
        Assert.Contains("holds no claim", Assert.Throws<RegistryException>(() => LiveCommands.WithdrawJson("esv::A::Taken", false, null, live)).Message);
    }

    [Fact]
    public void A_commit_refuses_an_entry_whose_sentence_names_the_work()
    {
        var live = Live();
        var claim = Claim("esv::A::Told");
        claim.Entry["does"] = "Found by the overseer.";
        live.Note(claim);
        Assert.Empty(live.Commit(store, TextWriter.Null));
        Assert.Null(store.Get("esv::A::Told"));
    }
}
