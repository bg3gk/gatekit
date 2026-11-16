using System.Text.Json.Nodes;
using GateKit.Json;
using GateKit.Symbols.Decomp;
using GateKit.Symbols.Registry;

namespace GateKit.Symbols.UnitTests;

/// <summary>What the registry's tools do by themselves when an entry is written: the source of a name,
/// the rung its evidence carries, duplicates, addresses in sentences, sentences said twice; and what
/// keeps a correction from being lost: amending, a commit that merges once, renaming, removing, ids
/// that differ only by case, the digest.</summary>
public sealed class RegistryRulesTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("registry-rules");
    readonly RegistryStore store;
    readonly LiveLayer live;

    public RegistryRulesTests()
    {
        store = new RegistryStore(Path.Combine(root, "repo", "registry"));
        live = new LiveLayer(Path.Combine(root, "home", "state", "registry-live"));
        Directory.CreateDirectory(store.Root);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    sealed class Stores(params (string Id, StoreName Name)[] held) : INameStores
    {
        public IReadOnlyList<StoreName> Of(EntryKind kind, string id, IReadOnlyList<(string Binary, long Rva)> places) => [.. held.Where(h => h.Id == id).Select(h => h.Name)];
        public bool InCode(string binary, long rva) => rva is >= 0x500000 and < 0x600000;
    }

    static JsonObject Evidence(string kind, string says = "it does what the entry says", string? tie = null)
    {
        var o = new JsonObject { ["kind"] = kind };
        if (tie is not null) { o["store"] = "EoCShared.dll"; o["tie"] = tie; }
        o["says"] = says;
        return o;
    }

    static JsonObject Function(string id, string rung, params JsonObject[] evidence) =>
        new() { ["id"] = id, ["rung"] = rung, ["does"] = "Rolls the dice of one request.", ["evidence"] = new JsonArray([.. evidence]) };

    DecompSession Session(TextWriter output) =>
        new(new DecompSettings(Path.Combine(root, "work"), Path.Combine(root, "tools"), Path.Combine(root, "game"), Path.Combine(root, "toolkit"), Path.Combine(root, "symbols.sqlite"), null), output, TextWriter.Null);

    Claim Note(NoteRequest request, INameStores? names = null, TextWriter? output = null)
    {
        using var s = Session(output ?? TextWriter.Null);
        return LiveCommands.Note(s, request with { Who = "a", Task = "t" }, live, store, names ?? new Stores());
    }

    // --- the bar ---

    [Theory]
    [InlineData("read", true, "inferred")]                          // a reading alone
    [InlineData("watch", true, "inferred")]
    [InlineData("read,reach", true, "inferred")]                    // a counter of reach lifts nothing
    [InlineData("read,watch", true, "confirmed")]
    [InlineData("test", true, "confirmed")]
    [InlineData("toolkit-name", true, "inferred")]                  // a store's name nothing ties to the function
    [InlineData("toolkit-name,read", true, "toolkit")]
    [InlineData("game-name,toolkit-name,read", true, "game")]
    [InlineData("game-name", false, "game")]                        // a type or a field carries its name itself
    [InlineData("pairing,text,extender-name", true, "inferred")]
    public void The_highest_rung_is_what_the_kinds_of_evidence_carry(string kinds, bool code, string ceiling)
    {
        var evidence = new JsonArray([.. kinds.Split(',').Select(k => (JsonNode)Evidence(k))]);
        Assert.Equal(ceiling, Rungs.Ceiling(evidence, code, out _));
    }

    [Fact]
    public void A_store_s_own_match_ties_its_name_and_a_confirmed_name_takes_the_store_s_rung()
    {
        Assert.Equal("toolkit", Rungs.Ceiling(new JsonArray(Evidence("toolkit-name", tie: "match")), true, out _));
        Assert.Equal("inferred", Rungs.Ceiling(new JsonArray(Evidence("toolkit-name", tie: "name")), true, out _));
        Assert.Equal("toolkit", Rungs.Settle("confirmed", new JsonArray(Evidence("toolkit-name", tie: "match"), Evidence("read"), Evidence("watch")), true, out var why));
        Assert.NotNull(why);
        Assert.Equal("inferred", Rungs.Settle("inferred", new JsonArray(Evidence("toolkit-name", tie: "match")), true, out why));   // the writer's doubt stands
        Assert.Null(why);
        Assert.Equal("extender", Rungs.Settle("extender", null, true, out _));
    }

    [Fact]
    public void A_rung_asked_above_its_evidence_is_lowered_with_a_line_and_never_refused()
    {
        var output = new StringWriter();
        var claim = Note(new NoteRequest("unplaced::CountsDice") { Rung = "confirmed", Does = "Counts the dice.", Evidence = ["read:the loop adds one a die", "reach:a counter shows it called only while a fight is on"] }, output: output);
        Assert.Equal("inferred", claim.Rung);
        Assert.Contains("lowered: unplaced::CountsDice confirmed -> inferred", output.ToString());
        Assert.Contains(new Journal(live.Folder).Since(null), l => l.Act == "lowered" && l.Id == "unplaced::CountsDice");
        // the check holds an entry written by hand to the same bar, as a note
        store.Write(EntryKind.Function, Function("esv::A::ByHand", "confirmed", Evidence("read")), create: false);
        var report = RegistryCheck.Run(store);
        Assert.Empty(report.Refusals);
        Assert.Contains(report.Notes, n => n.Rule == "rung" && n.Where.Contains("ByHand", StringComparison.Ordinal));
    }

    // --- the source of a name ---

    [Fact]
    public void The_tool_writes_where_a_name_comes_from_once_and_proposes_a_store_s_other_name()
    {
        var names = new Stores(
            ("esv::A::Rolls", new StoreName("toolkit-name", "EoCServer.dll", "esv::A::Rolls", "match", "strings")),
            ("esv::A::Rolls", new StoreName("game-name", "bg3_macos_x64", "esv::A::Rolls", "name")),
            ("unplaced::Ours", new StoreName("toolkit-name", "EoCShared.dll", "eoc::spell::Theirs", "match", "calls")));
        var output = new StringWriter();
        var claim = Note(new NoteRequest("esv::A::Rolls") { Rung = "confirmed", Does = "Rolls.", Evidence = ["read:it draws a die", "watch:the count rose by one"] }, names, output);
        var evidence = claim.Entry["evidence"]!.AsArray();
        var toolkit = evidence.Single(e => (string?)e!["kind"] == "toolkit-name")!;
        Assert.Equal(("EoCServer.dll", "match"), ((string?)toolkit["store"], (string?)toolkit["tie"]));
        Assert.Contains("matches the export to this function by strings", (string?)toolkit["says"]);
        Assert.Equal("game", claim.Rung);                                                    // both stores hold it: the higher rung
        Assert.Contains("name found: esv::A::Rolls - EoCServer.dll", output.ToString());
        // again: nothing twice
        Assert.Equal(4, Note(new NoteRequest("esv::A::Rolls") { Ask = ["Which roll reaches it?"] }, names).Entry["evidence"]!.AsArray().Count);

        output = new StringWriter();
        var ours = Note(new NoteRequest("unplaced::Ours") { Rung = "inferred", Does = "Checks a spell roll.", Evidence = ["read:it looks the condition up"] }, names, output);
        Assert.Equal("eoc::spell::Theirs", (string?)ours.Entry["known_as"]![0]!["name"]);
        Assert.Contains("another name: unplaced::Ours - EoCShared.dll names this function eoc::spell::Theirs", output.ToString());
        Assert.Equal("ours", NameSources.Whose(ours.Entry));
        Assert.Equal("the game's", NameSources.Whose(claim.Entry));
    }

    [Fact]
    public void An_evidence_of_a_name_written_by_hand_is_left_to_speak_alone()
    {
        var entry = Function("esv::A::Rolls", "toolkit", Evidence("toolkit-name", "EoCServer.dll exports this name; tied by the job its read shows"), Evidence("read"));
        var journal = new Journal(null);
        Assert.False(NameSources.Apply(entry, EntryKind.Function, [new StoreName("toolkit-name", "EoCServer.dll", "esv::A::Rolls", "match")], journal, DateTime.UtcNow));
        Assert.Empty(journal.Lines);
    }

    // --- a field of a type ---

    [Fact]
    public void A_field_carries_its_own_rung_and_its_own_evidence()
    {
        var claim = Note(new NoteRequest("eoc::T")
        {
            Kind = "type", Rung = "game", About = "A stream of rolls.", Evidence = ["game-name:the executable keeps the type's name"],
            Fields = ["0x70:NumRolls:uint64"], FieldRung = "confirmed", FieldEvidence = ["read:the draw adds one to it", "watch:a write watch caught the draw"],
        });
        var field = claim.Entry["fields"]![0]!;
        Assert.Equal("confirmed", (string?)field["rung"]);
        Assert.Equal(["read", "watch"], field["evidence"]!.AsArray().Select(e => (string?)e!["kind"]));
        // a field with no evidence of its own is not lifted by the type's
        var output = new StringWriter();
        var bare = Note(new NoteRequest("eoc::T") { Kind = "type", Fields = ["0x78:Seed"] }, output: output);
        var seed = bare.Entry["fields"]!.AsArray().Single(f => (string?)f!["name"] == "Seed")!;
        Assert.Equal("inferred", (string?)seed["rung"]);
        Assert.Null(seed["evidence"]);
        Assert.Contains("lowered: eoc::T.Seed game -> inferred", output.ToString());
        Assert.Equal("confirmed", (string?)bare.Entry["fields"]![0]!["rung"]);
    }

    // --- corrections ---

    [Fact]
    public void An_amended_sentence_takes_the_place_of_the_old_one_and_a_sentence_is_never_said_twice()
    {
        Note(new NoteRequest("unplaced::F") { Rung = "inferred", Does = "Rolls.", Evidence = ["read:it draws two dice"], Ask = ["What calls it?"] });
        Note(new NoteRequest("unplaced::F") { Evidence = ["read:it draws two dice"], Ask = ["What calls it?"] });
        var amended = Note(new NoteRequest("unplaced::F") { Amend = true, Evidence = ["read:it draws one die"], Ask = ["Which roll calls it?"] });
        Assert.Equal("it draws one die", (string?)Assert.Single(amended.Entry["evidence"]!.AsArray())!["says"]);
        Assert.Equal("Which roll calls it?", (string?)Assert.Single(amended.Entry["questions"]!.AsArray())!["ask"]);
    }

    [Fact]
    public void A_commit_merges_a_claim_once_and_does_not_undo_what_was_written_into_the_entry_since()
    {
        Note(new NoteRequest("unplaced::F") { Rung = "inferred", Does = "Rolls.", Evidence = ["read:it draws two dice"], Ask = ["What calls it?"] });
        Assert.Equal(["unplaced::F"], live.Commit(store, TextWriter.Null));
        var edited = (JsonObject)store.Get("unplaced::F")!.Json.DeepClone();
        edited["does"] = "Rolls the dice of one request.";
        store.Write(EntryKind.Function, edited, create: false);
        Assert.Empty(live.Commit(store, TextWriter.Null));
        Assert.Equal("Rolls the dice of one request.", (string?)store.Get("unplaced::F")!.Json["does"]);
        // the next note builds on the entry as it stands, not on the claim before the edit
        var next = Note(new NoteRequest("unplaced::F") { Ask = ["How often?"] });
        Assert.Equal("Rolls the dice of one request.", (string?)next.Entry["does"]);
        // a dry run writes nothing and says what it would
        var output = new StringWriter();
        Assert.Equal(["unplaced::F"], live.Commit(store, output, dry: true));
        Assert.Contains("would commit: unplaced::F", output.ToString());
        Assert.Single(store.Get("unplaced::F")!.Json["questions"]!.AsArray());
    }

    // --- at commit ---

    Claim Placed(string id, long rva, JsonObject entry) => new(EntryKind.Function, entry, "game.exe", rva, "777", 0x40, "a", DateTime.UtcNow, "t", null);

    [Fact]
    public void An_unnamed_id_and_a_named_one_at_one_address_become_the_named_entry()
    {
        var unnamed = Function("unplaced::unnamed_0a1b2c3d", "unknown", Evidence("watch", "a count-only probe shows it calling the roller"));
        unnamed["questions"] = new JsonArray(new JsonObject { ["ask"] = "What is this function for?", ["about"] = "meaning", ["state"] = "open" });
        live.Note(Placed("unplaced::unnamed_0a1b2c3d", 0x100, unnamed));
        Assert.Single(live.Commit(store, TextWriter.Null));
        File.WriteAllText(Path.Combine(store.Repository, "notes.md"), "Called from code:unplaced::unnamed_0a1b2c3d.\n");
        live.Note(Placed("esv::A::Rolls", 0x100, Function("esv::A::Rolls", "inferred", Evidence("read"))));
        Assert.Empty(live.Clashes(store));                                               // no clash: one thing, with and without its name
        var output = new StringWriter();
        live.Commit(store, output);
        Assert.Contains("folded: unplaced::unnamed_0a1b2c3d into esv::A::Rolls", output.ToString());
        Assert.Null(store.Get("unplaced::unnamed_0a1b2c3d"));
        Assert.Equal("esv::A::Rolls", Assert.Single(store.Table("777", "game.exe")).Id);
        var kept = store.Get("esv::A::Rolls")!.Json;
        Assert.Contains(kept["evidence"]!.AsArray(), e => (string?)e!["says"] == "a count-only probe shows it calling the roller");
        Assert.Null(kept["questions"]);                                                  // what it is, the name answers
        Assert.Equal("Called from code:esv::A::Rolls.\n", File.ReadAllText(Path.Combine(store.Repository, "notes.md")));
        Assert.Empty(live.Of("unplaced::unnamed_0a1b2c3d"));
    }

    [Fact]
    public void An_address_of_a_named_function_becomes_its_id_and_any_other_is_kept_with_its_binary_and_build()
    {
        store.Write(EntryKind.Function, Function("esv::A::Callee", "inferred", Evidence("read")), create: false);
        store.Write(EntryKind.Function, Function("unplaced::unnamed_0a1b2c3d", "unknown"), create: false);
        store.WriteTable("777", "game.exe", [new BuildRow("esv::A::Callee", 0x12340, 0x40, null), new BuildRow("unplaced::unnamed_0a1b2c3d", 0x23450, 0x40, null)]);
        var entry = Function("esv::A::Caller", "inferred",
            Evidence("read", "calls game.exe+0x12340, then 0x12340 again from +0x54321A, and FUN_140012340; the callee 0x23450 has no name; the mask is 0x7FFFFFFF; Toolkit.dll+0x999999 is the twin"),
            Evidence("read", "calls game.exe+0x12340, then 0x12340 again from +0x54321A, and FUN_140012340; the callee 0x23450 has no name; the mask is 0x7FFFFFFF; Toolkit.dll+0x999999 is the twin"));
        entry["does"] = "Calls the roller at +0x12340.";
        live.Note(Placed("esv::A::Caller", 0x543200, entry));
        var output = new StringWriter();
        live.Commit(store, output, new Stores());
        var written = store.Get("esv::A::Caller")!.Json;
        Assert.Equal("Calls the roller at code:esv::A::Callee.", (string?)written["does"]);
        Assert.Equal("calls code:esv::A::Callee, then code:esv::A::Callee again from game.exe@777+0x54321A, and code:esv::A::Callee; the callee game.exe@777+0x23450 has no name; the mask is 0x7FFFFFFF; Toolkit.dll+0x999999 is the twin",
            (string?)Assert.Single(written["evidence"]!.AsArray())!["says"]);              // and the sentence said twice is kept once
        Assert.Contains("4 turned into ids, 2 written with binary and build, 1 not placed", output.ToString());
        Assert.Contains("1 sentence of evidence said twice", output.ToString());
        Assert.Empty(RegistryCheck.Run(store).Refusals);
        Assert.Single(RegistryCheck.Run(store).Notes, n => n.Rule == "address");          // the Toolkit's address alone lacks its build
        Assert.Equal("game.exe+0x54321A", LiveCommands.Plain("game.exe@777+0x54321A"));
    }

    [Fact]
    public void A_reanchor_carries_an_address_in_a_sentence_with_its_function_or_marks_it_stale()
    {
        var entry = Function("esv::A::Caller", "inferred", Evidence("read", "called from game.exe@777+0x1010 and from game.exe@777+0x2010"));
        store.Write(EntryKind.Function, entry, create: false);
        var source = new List<BuildRow> { new("esv::A::Caller", 0x1000, 0x40, "b"), new("esv::A::Other", 0x2000, 0x40, "c") };
        var moved = new List<Moved> { new("esv::A::Caller", 0x1000, new BuildRow("esv::A::Caller", 0x5000, 0x40, "b"), "seed", true), new("esv::A::Other", 0x2000, new BuildRow("esv::A::Other", 0x6000, 0x48, "d"), "seed", false) };
        var journal = new Journal(null);
        Assert.Equal((1, 1), new EntryRules(store, null, journal).ReanchorProse("game.exe", "777", "888", source, moved, dry: false));
        Assert.Equal("called from game.exe@888+0x5010 and from game.exe@777+0x2010", (string?)store.Get("esv::A::Caller")!.Json["evidence"]![0]!["says"]);
        Assert.Single(journal.Lines, l => l.Act == "stale");
    }

    // --- the check that remembers unchanged files ---

    static string Verdict(CheckReport report) => string.Join("\n",
        report.Refusals.Select(r => $"refused {r}").Concat(report.Owed.Select(o => $"owed {o}")).Concat(report.Notes.Select(n => $"note {n}")));

    [Fact]
    public void An_unchanged_entry_is_judged_again_when_what_it_cites_changes()
    {
        Note(new NoteRequest("unplaced::Old") { Rung = "inferred", Does = "Rolls.", Evidence = ["read:it draws"] });
        Note(new NoteRequest("esv::A::Caller") { Rung = "inferred", Does = "Calls code:unplaced::Old; test:combat/hit shows it.", Evidence = ["read:it calls it twice"] });
        live.Commit(store, TextWriter.Null);
        var test = Path.Combine(store.Repository, "tests", "combat", "hit.lua");
        Directory.CreateDirectory(Path.GetDirectoryName(test)!);
        File.WriteAllText(test, "function run(game) end\n");
        store.WriteTable("777", "game.exe", [new BuildRow("unplaced::Old", 0x10, 8, null)]);
        var data = Path.Combine(store.Repository, "mods", "M", "Server", "RollOrigins.json");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!);
        var origins = JsonNode.Parse("""{"schema": "bridge/roll-origins/1", "askers": [{"id": "unplaced::Old", "what": "a roll"}], "causes": [], "statusApplied": {"id": "esv::A::Caller"}}""")!.AsObject();
        origins["places"] = LiveCommands.PlacesOf(store, origins).Places;
        File.WriteAllText(data, origins.ToJsonString());
        var memory = Path.Combine(root, "home", "cache", "registry-check");
        var caller = store.Relative(store.Get("esv::A::Caller")!.Path);

        // every time: what the remembering check says is what the whole check says
        (CheckReport Report, CheckCache Cache) Remembering()
        {
            var cache = CheckCache.Open(memory, store.Repository, full: false);
            var report = RegistryCheck.Run(store, cache);
            cache.Save();
            Assert.Equal(Verdict(RegistryCheck.Run(store)), Verdict(report));
            return (report, cache);
        }

        var (first, _) = Remembering();
        Assert.DoesNotContain(first.Refusals, r => r.Rule is "cite-code" or "cite-test" or "places" or "table");
        var (again, kept) = Remembering();
        Assert.Equal(0, kept.Read);                                                               // nothing read again
        Assert.Equal(Verdict(first), Verdict(again));

        // a test removed: the caller's file is the same, its citation of the test is refused
        File.Delete(test);
        var (noTest, cache) = Remembering();
        Assert.Contains(noTest.Refusals, r => r.Rule == "cite-test" && r.Where.Contains("Caller", StringComparison.Ordinal));
        Assert.Equal(0, cache.Read);

        // a build table's row gone: the data file of the mod is the same, its places are refused
        store.WriteTable("777", "game.exe", []);
        Assert.Contains(Remembering().Report.Refusals, r => r.Rule == "places");

        // the cited entry renamed by hand (its file moved, the caller not rewritten): the caller's citation is refused
        var old = store.Get("unplaced::Old")!;
        var moved = (JsonObject)old.Json.DeepClone();
        moved.Remove("schema");
        moved["id"] = "unplaced::New";
        store.Write(EntryKind.Function, moved, create: true);
        store.Delete(old);
        var (renamed, read) = Remembering();
        Assert.Contains(renamed.Refusals, r => r.Rule == "cite-code" && r.What.Contains("no entry unplaced::Old", StringComparison.Ordinal));
        Assert.DoesNotContain(renamed.Refusals, r => r.Where.StartsWith(caller, StringComparison.Ordinal) && r.Rule != "cite-code" && r.Rule != "cite-test");
        Assert.True(read.Read <= 2, $"{read.Read} files read: only the new entry's file, as an entry and as text");

        // --full reads everything again and says the same
        var full = CheckCache.Open(memory, store.Repository, full: true);
        Assert.Equal(Verdict(renamed), Verdict(RegistryCheck.Run(store, full)));
        Assert.Equal(0, full.Kept);
    }

    // --- rename, remove ---

    [Fact]
    public void A_rename_takes_the_live_claims_and_the_data_files_that_hold_places_with_it()
    {
        Note(new NoteRequest("unplaced::Old") { Rung = "inferred", Does = "Rolls.", Evidence = ["read:it draws"], Owner = "esv::RollSystem" });
        Note(new NoteRequest("esv::A::Caller") { Rung = "inferred", Does = "Calls code:unplaced::Old.", Evidence = ["read:it calls code:unplaced::Old twice"] });
        live.Commit(store, TextWriter.Null);
        store.WriteTable("777", "game.exe", [new BuildRow("unplaced::Old", 0x10, 8, null)]);
        var data = Path.Combine(store.Repository, "mods", "M", "Server", "RollOrigins.json");
        Directory.CreateDirectory(Path.GetDirectoryName(data)!);
        File.WriteAllText(data, """{"schema": "bridge/roll-origins/1", "askers": [{"id": "unplaced::Old", "what": "a roll"}], "causes": [], "statusApplied": {"id": "esv::A::Caller"}, "places": {}}""");
        Assert.Equal("esv::RollSystem", (string?)store.Get("unplaced::Old")!.Json["owner"]);

        RegistryRefs.Rename(store, "unplaced::Old", "esv::RollSystem::New", TextWriter.Null, live);

        Assert.Empty(live.Of("unplaced::Old"));
        Assert.Equal("esv::RollSystem::New", live.Of("esv::RollSystem::New").Last().Id);
        Assert.Contains("code:esv::RollSystem::New", (string?)live.Of("esv::A::Caller").Last().Entry["does"]);
        Assert.Null(store.Get("esv::RollSystem::New")!.Json["owner"]);                    // placed: the scope says the owner
        var file = JsonNode.Parse(File.ReadAllText(data))!;
        Assert.Equal("esv::RollSystem::New", (string?)file["askers"]![0]!["id"]);
        Assert.NotNull(file["places"]!["777"]!["game.exe"]!["esv::RollSystem::New"]);
        Assert.Empty(live.Commit(store, TextWriter.Null));                               // the old id does not come back
        Assert.Null(store.Get("unplaced::Old"));
    }

    [Fact]
    public void An_entry_is_removed_with_its_rows_and_claims_and_refused_while_cited()
    {
        Note(new NoteRequest("unplaced::Gone") { Rung = "inferred", Does = "Rolls.", Evidence = ["read:it draws"] });
        live.Commit(store, TextWriter.Null);
        store.WriteTable("777", "game.exe", [new BuildRow("unplaced::Gone", 0x10, 8, null)]);
        var notes = Path.Combine(store.Repository, "notes.md");
        File.WriteAllText(notes, "See code:unplaced::Gone.\n");
        Assert.Contains("is cited", Assert.Throws<RegistryException>(() => RegistryRefs.Remove(store, "unplaced::Gone", live, TextWriter.Null)).Message);
        File.Delete(notes);
        RegistryRefs.Remove(store, "unplaced::Gone", live, TextWriter.Null);
        Assert.Null(store.Get("unplaced::Gone"));
        Assert.Empty(store.Table("777", "game.exe"));
        Assert.Empty(live.Of("unplaced::Gone"));
        Assert.Empty(live.Commit(store, TextWriter.Null));
    }

    // --- the owner of a name of ours ---

    [Fact]
    public void An_unplaced_entry_names_its_owner_and_is_listed_under_the_owner_s_scope()
    {
        var output = new StringWriter();
        Note(new NoteRequest("unplaced::CombatLogProtocolAddEntries") { Rung = "inferred", Does = "Adds entries.", Evidence = ["read:it appends"], Owner = "esv::CombatLogProtocol" });
        Note(new NoteRequest("esv::A::Placed") { Rung = "inferred", Does = "Rolls.", Evidence = ["read:it draws"], Owner = "esv::A" }, output: output);
        Assert.Contains("owner left out", output.ToString());
        live.Commit(store, TextWriter.Null);
        var entry = store.Get("unplaced::CombatLogProtocolAddEntries")!;
        Assert.True(Digest.InScope(entry, "esv::CombatLogProtocol"));
        Assert.False(Digest.InScope(entry, "esv::combat"));
        Assert.Null(store.Get("esv::A::Placed")!.Json["owner"]);
        Assert.Equal("ours", (string?)LiveCommands.ShowJson("unplaced::CombatLogProtocolAddEntries", store, live)["name"]);
    }

    // --- ids that differ only by case ---

    [Fact]
    public void A_namespace_and_a_class_that_differ_only_by_case_both_live_under_their_ids()
    {
        // whichever comes first: the name that sorts first keeps its folder, the other's differing letters are written %XX
        foreach (var order in new[] { new[] { "esv::Status::Enter", "esv::status::RequestSystem::Gather" }, ["esv::status::RequestSystem::Gather", "esv::Status::Enter"] })
        {
            var s = new RegistryStore(Path.Combine(root, "case-" + order[0].Length, "registry"));
            foreach (var id in order) s.Write(EntryKind.Function, Function(id, "inferred"), create: true);
            Assert.Equal("functions/esv/Status/Enter.json", s.Relative(s.PathOf(EntryKind.Function, "esv::Status::Enter")));
            Assert.Equal("functions/esv/%73tatus/RequestSystem/Gather.json", s.Relative(s.PathOf(EntryKind.Function, "esv::status::RequestSystem::Gather")));
            Assert.Equal("esv::status::RequestSystem::Gather", RegistryId.FromPath("esv/%73tatus/RequestSystem/Gather.json"));
            Assert.Equal("esv::Status::Enter", s.Get("esv::Status::Enter")!.Id);
            Assert.Equal("esv::status::RequestSystem::Gather", s.Get("esv::status::RequestSystem::Gather")!.Id);
            Assert.Null(s.Get("esv::STATUS::Enter"));
            Assert.Empty(RegistryCheck.Run(s).Refusals);
            Assert.Equal(2, s.All().Count);
            // the class gone, the namespace takes its plain folder back
            s.Delete(s.Get("esv::Status::Enter")!);
            Assert.Equal("functions/esv/status/RequestSystem/Gather.json", s.Relative(s.Get("esv::status::RequestSystem::Gather")!.Path));
            Assert.Empty(RegistryCheck.Run(s).Refusals);
        }
        live.Note(Placed("esv::Status::Enter", 0x10, Function("esv::Status::Enter", "inferred")));
        live.Note(Placed("esv::status::Enter", 0x20, Function("esv::status::Enter", "inferred")));
        Assert.Equal("esv::Status::Enter", Assert.Single(live.Of("esv::Status::Enter")).Id);
        Assert.Equal("esv::status::Enter", Assert.Single(live.Of("esv::status::Enter")).Id);
    }

    // --- the digest ---

    [Fact]
    public void The_digest_says_what_the_tools_did_and_lists_only_what_a_person_must_decide()
    {
        var names = new Stores(("unplaced::Ours", new StoreName("toolkit-name", "EoCShared.dll", "eoc::spell::Theirs", "match", "calls")),
            ("esv::A::Rolls", new StoreName("toolkit-name", "EoCServer.dll", "esv::A::Rolls", "match", "strings")));
        Note(new NoteRequest("unplaced::Ours") { Rung = "confirmed", Does = "Checks a spell roll.", Evidence = ["read:it looks the condition up"] }, names);
        Note(new NoteRequest("esv::A::Rolls") { Rung = "confirmed", Does = "Rolls.", Evidence = ["read:it draws", "watch:the count rose"] }, names);
        live.Note(Placed("esv::B::First", 0x20, Function("esv::B::First", "inferred")));
        live.Note(Placed("esv::B::Second", 0x20, Function("esv::B::Second", "inferred")));
        live.Commit(store, TextWriter.Null, names);
        var digest = Digest.Of(store, live, new Journal(live.Folder), null, null);
        Assert.Equal(1, (int)digest["lowered"]!["count"]!);
        Assert.Equal(1, (int)digest["raised"]!["count"]!);
        Assert.Equal(1, (int)digest["names"]!["matched"]!);
        Assert.Single(digest["clashes"]!.AsArray());
        var person = digest["person"]!.AsArray().Select(p => (string?)p!["decide"]).ToList();
        Assert.Equal(2, person.Count);
        Assert.Contains(person, p => p!.Contains("gatekit registry settle", StringComparison.Ordinal));
        Assert.Contains(person, p => p!.Contains("names this function eoc::spell::Theirs", StringComparison.Ordinal));
        var page = new StringWriter();
        Digest.Print(page, digest);
        Assert.Contains("a person is needed: 2", page.ToString());
        Assert.True(page.ToString().Split('\n').Length < 20);
        // renamed as proposed, the line is gone
        live.Settle("game.exe", "777", 0x20, "esv::B::First", false, "a", "t");
        RegistryRefs.Rename(store, "unplaced::Ours", "eoc::spell::Theirs", TextWriter.Null, live);
        Assert.Empty(Digest.Of(store, live, new Journal(live.Folder), null, null)["person"]!.AsArray());
    }

    [Fact]
    public void Tidy_does_to_a_committed_entry_what_a_commit_does_to_a_note_and_a_dry_run_writes_nothing()
    {
        store.Write(EntryKind.Function, Function("esv::A::Rolls", "confirmed", Evidence("read"), Evidence("read")), create: false);
        var names = new Stores(("esv::A::Rolls", new StoreName("toolkit-name", "EoCServer.dll", "esv::A::Rolls", "name")));
        var output = new StringWriter();
        Digest.Tidy(output, store, live, names, null, null, dry: true);
        Assert.Contains("1 would change", output.ToString());
        Assert.Equal("confirmed", (string?)store.Get("esv::A::Rolls")!.Json["rung"]);
        Assert.Empty(new Journal(live.Folder).Since(null));
        Digest.Tidy(TextWriter.Null, store, live, names, "esv::A", null, dry: false);
        var entry = store.Get("esv::A::Rolls")!.Json;
        Assert.Equal("toolkit", (string?)entry["rung"]);                                 // the name is a store's and a read ties it
        Assert.Equal(["read", "toolkit-name"], entry["evidence"]!.AsArray().Select(e => (string?)e!["kind"]));
        output = new StringWriter();
        Digest.Tidy(output, store, live, names, null, null, dry: false);
        Assert.Contains("0 changed", output.ToString());                                 // again: nothing
    }

    // --- an address that is not a function's start ---

    /// <summary>The decompiler's database of the test: one function of game.exe at 0x1000, 0x100 bytes long.</summary>
    void OneFunction()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using var db = DecompStore.OpenWrite(Path.Combine(root, "work", "decomp.sqlite"));
        db.Execute("INSERT INTO binaries (id, name) VALUES (1, 'game.exe')");
        db.Execute("INSERT INTO functions (id, binary_id, rva, name, size, thunk) VALUES (1, 1, 4096, 'FUN_140001000', 256, 0)");
        db.Execute("INSERT INTO code VALUES (1, ?)", DecompStore.Compress("void FUN_140001000(void)\n\n{\n  return;\n}\n"));
    }

    [Fact]
    public void A_note_at_an_address_inside_a_function_is_refused_and_snap_takes_the_function()
    {
        OneFunction();
        var inside = Assert.Throws<RegistryException>(() => Note(new NoteRequest("toy::Roller::Roll") { At = "game.exe+0x1005", Evidence = ["read:it rolls"] }));
        Assert.Contains("not the start of a function", inside.Message);
        Assert.Contains("game.exe+0x1000", inside.Message);                               // it says where the function starts
        Assert.Empty(live.Of("toy::Roller::Roll"));                                        // nothing was written

        var output = new StringWriter();
        var snapped = Note(new NoteRequest("toy::Roller::Roll") { At = "game.exe+0x1005", Snap = true, Evidence = ["read:it rolls"] }, output: output);
        Assert.Equal(("game.exe", 0x1000L), (snapped.Binary, snapped.Rva));               // the note is of the function the address falls in
        Assert.Contains("snapped", output.ToString());

        var start = Note(new NoteRequest("toy::Other::Start") { At = "game.exe+0x1000", Evidence = ["read:it starts"] });
        Assert.Equal(0x1000L, start.Rva);                                                  // a function's own start is taken as it is
    }
}
