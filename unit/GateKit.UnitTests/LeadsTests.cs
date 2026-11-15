using System.Text.Json.Nodes;
using GateKit.Leads;
using Record = GateKit.Copies.Record;

namespace GateKit.UnitTests;

/// <summary>
/// The leads of the coverage hunt (src/GateKit.Leads). Two unattended runs spent 42 % of their play
/// stretches on types aimed at by the bare list of the unseen, and six on a teleport path the code
/// never takes; a lead is meant to carry what the code and the data say, built by rules that hold
/// still. These run on synthetic code, data and record - nothing of the game's.
/// </summary>
public sealed class LeadsTests : IDisposable
{
    readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A code graph in miniature: functions by address, with names and direct callers.</summary>
    sealed class FakeCode : ICodeIndex
    {
        public readonly Dictionary<long, string> Names = [];
        public readonly Dictionary<long, List<long>> CalledBy = [];
        public readonly Dictionary<string, List<Creator>> Adds = [];
        public readonly Dictionary<long, string> Updates = [];
        public readonly Dictionary<string, List<long>> SystemUsers = [];
        public readonly Dictionary<string, int> Users = [];

        public IReadOnlyList<Creator> Adders(string type) => Adds.GetValueOrDefault(type) ?? [];
        public IReadOnlyList<long> Callers(string binary, long rva) => CalledBy.GetValueOrDefault(rva) ?? [];
        public string? NameOf(string binary, long rva) => Names.GetValueOrDefault(rva);
        public string? UpdateOf(string binary, long rva) => Updates.GetValueOrDefault(rva);
        public IReadOnlyList<long> SystemIndexUsers(string binary, string system) => SystemUsers.GetValueOrDefault(system) ?? [];
        public int IndexUsers(string type) => Users.GetValueOrDefault(type, 7);
        public readonly Dictionary<(long Function, int Occurrence), string> Conditions = [];
        public readonly List<(string System, long Function, string Type, int Occurrence)> Asked = [];
        public string? CreationCondition(string binary, string system, long function, string type, int occurrence)
        {
            Asked.Add((system, function, type, occurrence));
            return Conditions.GetValueOrDefault((function, occurrence));
        }
    }

    sealed class FakeData : IDataIndex
    {
        public readonly Dictionary<string, List<StatUse>> Calls = [];
        public IReadOnlyList<StatUse> Carrying(string call, int limit, bool functor = false) => [.. (Calls.GetValueOrDefault(call) ?? []).Take(limit)];
        public int CountCarrying(string call, bool functor = false) => Calls.GetValueOrDefault(call)?.Count ?? 0;
        public IReadOnlyList<string> CallsHolding(string word, int limit, bool functor = false) => [.. Calls.Keys.Where(k => k.Contains(word, StringComparison.Ordinal)).Order().Take(limit)];
        public IReadOnlyList<(string Name, string Kind)> OsirisFunctions() => [("SetDoorOverride", "call"), ("DoorOpened", "event")];
        public int OsirisUses(string function) => function == "SetDoorOverride" ? 4 : 0;
        public string? DisplayName(string stat) => stat == "STATUS_HEAVY" ? "Heavy" : null;
    }

    sealed class FakeWiki : IWikiTitles
    {
        public int Asked;
        public Task<string?> PageFor(string name, CancellationToken cancel = default)
        {
            Asked++;
            return Task.FromResult<string?>(name == "Heavy" ? "Heavy" : null);
        }
    }

    const string W = "bg3_dx11.exe";

    string Checkout()
    {
        _temp.Write("GateKit.slnx", "<Solution />");
        _temp.Write("research/coverage/coverage.json", """
            {"components": [
              {"name": "esv::door::OverrideComponent", "side": "server", "mapped": true},
              {"name": "esv::jump::StartRequestOneFrameComponent", "side": "server", "mapped": true, "oneFrame": true},
              {"name": "eoc::WeightBoostComponent", "side": "server", "mapped": true},
              {"name": "ls::ugc::DownloadResultOneFrameComponent", "side": "client", "mapped": true},
              {"name": "esv::ghost::LonelyComponent", "side": "server", "mapped": true},
              {"name": "esv::seen::AlreadyComponent", "side": "server", "mapped": true}]}
            """);
        _temp.Write("research/coverage/seen/index.json", """{"esv::seen::AlreadyComponent": {"server": {"max": 1, "labels": []}}}""");
        _temp.Write("research/coverage/systems/esv__jump/JumpSystem.json", """
            {"system": "esv::jump::JumpSystem", "symbols": {"uses": [
              {"type": "esv::jump::StartRequestOneFrameComponent", "kind": "add", "function": "bg3_dx11.exe+0x500", "site": "bg3_dx11.exe+0x510", "depth": 2, "confidence": "high"},
              {"type": "esv::jump::JumperComponent", "kind": "get", "function": "bg3_dx11.exe+0x500", "site": "bg3_dx11.exe+0x520", "depth": 2, "confidence": "high"}]}}
            """);
        _temp.Write("research/coverage/runs.jsonl",
            """{"ended":"2026-11-13 19:10","run":"r1","half":"world","found":"[]","open":"Cast Jump twice: no esv::jump type appeared."}""" + "\n"
            + """{"ended":"2026-11-13 22:10","run":"r2","half":"world","found":"[]","open":"Jumped across the gap again, still nothing of esv::jump::{StartRequestOneFrame}. Next: try the jump of a summon."}""" + "\n");
        return _temp["research/coverage"];
    }

    (FakeCode, FakeData) Facts()
    {
        var code = new FakeCode();
        // door: an Osiris function two calls above the creator
        code.Adds["esv::door::OverrideComponent"] = [new Creator(null, $"{W}+0x100", W, null, null, "high")];
        code.CalledBy[0x100] = [0x200];
        code.CalledBy[0x200] = [0x300];
        code.Names[0x300] = "esv::OsirisCharacterFunctions::SetDoorOverride";
        // jump: created in a system's update, and the system is handed its work by a stats functor
        code.Updates[0x500] = "esv::jump::JumpSystem";
        code.SystemUsers["esv::jump::JumpSystem"] = [0x900];
        code.CalledBy[0x900] = [0x910];
        code.Names[0x910] = "esv::functor::StatsFunctorJump::Execute";
        // the lonely one: nothing creates it and only its registration reads its index
        code.Users["esv::ghost::LonelyComponent"] = 2;
        var data = new FakeData();
        data.Calls["Weight"] = [new StatUse("STATUS_HEAVY", "StatusData", "Boosts", "Shared"), new StatUse("DEV_WEIGHT", "PassiveData", "Boosts", "SharedDev")];
        data.Calls["Jump"] = [new StatUse("Projectile_Jump", "SpellData", "SpellProperties", "Shared")];
        return (code, data);
    }

    static JsonObject Row(List<JsonObject> rows, string type) => rows.Single(r => (string?)r["type"] == type);

    [Fact]
    public async Task A_lead_names_its_entry_from_the_walk_up_the_callers_and_says_unknown_otherwise()
    {
        var root = Checkout();
        var (code, data) = Facts();
        var wiki = new FakeWiki();
        var rows = await new Builder(root, code, data, wiki).Build([]);

        Assert.Equal(5, rows.Count);   // the seen type has no lead
        var door = Row(rows, "esv::door::OverrideComponent");
        Assert.Equal("osiris", (string?)door["entry"]!["kind"]);
        Assert.Equal("SetDoorOverride", (string?)door["entry"]!["detail"]);
        Assert.Equal(2, (int?)door["entry"]!["depth"]);
        Assert.Equal("osiris", (string?)door["instrument"]);
        Assert.Contains("Osi.SetDoorOverride", (string?)door["how"]);

        // Created in a system's update; the walk goes on through who reads the system's index.
        var jump = Row(rows, "esv::jump::StartRequestOneFrameComponent");
        Assert.Equal("gameplay_event", (string?)jump["class"]);
        Assert.True((bool?)jump["one_frame"]);
        Assert.Equal("functor", (string?)jump["entry"]!["kind"]);
        Assert.Equal("queue", (string?)jump["entry"]!["basis"]);
        Assert.Equal(["esv::jump::JumperComponent"], jump["entry"]!["guard"]!.AsArray().Select(g => (string)g!));
        Assert.Equal("esv::jump::JumpSystem", (string?)jump["creators"]![0]!["system"]);
        Assert.Contains("Projectile_Jump", jump["data"]!.ToJsonString());

        // A boost: the data that carries it, shipping first, and the wiki page of the example.
        var weight = Row(rows, "eoc::WeightBoostComponent");
        Assert.Equal("boost", (string?)weight["class"]);
        Assert.Equal("unknown", (string?)weight["entry"]!["kind"]);   // nothing creates it in this code: not guessed
        Assert.Equal("unknown", (string?)weight["reachable"]);
        Assert.Contains("STATUS_HEAVY", (string?)weight["how"]);
        Assert.Equal("osiris", (string?)weight["instrument"]);
        Assert.Equal("Heavy", (string?)weight["wiki"]);

        Assert.Equal("online_ugc", (string?)Row(rows, "ls::ugc::DownloadResultOneFrameComponent")["class"]);
        Assert.Equal("network", (string?)Row(rows, "ls::ugc::DownloadResultOneFrameComponent")["instrument"]);
        var lonely = Row(rows, "esv::ghost::LonelyComponent");
        Assert.Equal("not_in_shipping_game", (string?)lonely["reachable"]);
        Assert.Equal("unreachable", (string?)lonely["state"]);
        Assert.Equal("unknown", (string?)lonely["entry"]!["kind"]);

        // The runs' prose: two empty tries with play, the same type through a brace list; the
        // sentence about what to do next is no try. The jump lead was tried twice with its own
        // instrument? Its instrument is play (a spell carries the functor), so it is parked.
        Assert.Equal(2, jump["attempts"]!.AsArray().Count);
        Assert.Equal("parked", (string?)jump["state"]);
        Assert.True((int)door["score"]! > (int)jump["score"]!);

        var tally = Lead.Tally(rows);
        Assert.Equal(4, (int)tally["ceiling"]!);
        Assert.Equal(2, (int)tally["with_named_entry"]!);
    }

    [Fact]
    public async Task A_system_update_lead_says_the_condition_of_the_creating_call()
    {
        var root = Checkout();
        var (code, data) = Facts();
        code.SystemUsers.Clear();                      // no queue: the system's update is the entry
        code.Conditions[(0x500, 0)] = "has(esv::jump::JumperComponent) and esv::jump::JumperComponent.State == 2";
        var rows = await new Builder(root, code, data, null).Build([]);
        var jump = Row(rows, "esv::jump::StartRequestOneFrameComponent");
        Assert.Equal("system_update", (string?)jump["entry"]!["kind"]);
        Assert.Equal("has(esv::jump::JumperComponent) and esv::jump::JumperComponent.State == 2", (string?)jump["entry"]!["condition"]);
        Assert.Contains("when has(esv::jump::JumperComponent) and esv::jump::JumperComponent.State == 2", (string?)jump["how"]);
        Assert.DoesNotContain("Play while", (string?)jump["how"]);
        Assert.Contains(("esv::jump::JumpSystem", 0x500L, "esv::jump::StartRequestOneFrameComponent", 0), code.Asked);

        // a rebuild keeps the runs' tries and the fields set by hand beside the new condition
        rows[rows.IndexOf(jump)]["note"] = "by hand";
        var again = await new Builder(root, code, data, null).Build(rows);
        var kept = Row(again, "esv::jump::StartRequestOneFrameComponent");
        Assert.Equal("by hand", (string?)kept["note"]);
        Assert.Equal(2, kept["attempts"]!.AsArray().Count);
        Assert.NotNull(kept["entry"]!["condition"]);

        // nothing extracted: the sentence of before
        code.Conditions.Clear();
        var plain = Row(await new Builder(root, code, data, null).Build([]), "esv::jump::StartRequestOneFrameComponent");
        Assert.Null(plain["entry"]!["condition"]);
        Assert.Contains("Play while", (string?)plain["how"]);
    }

    [Fact]
    public async Task A_rebuild_gives_the_same_file_and_keeps_what_the_runs_reported()
    {
        var root = Checkout();
        var (code, data) = Facts();
        using (Record.Lock(root)) Lead.Write(root, await new Builder(root, code, data, null).Build([]));
        var first = File.ReadAllText(Lead.PathOf(root));
        Assert.DoesNotContain("\r", first);

        Lead.Report(root, "esv::door::OverrideComponent", "empty", "called it on Astarion, nothing", "world", null, "run-1");
        var rows = Lead.Read(root);
        Row(rows, "esv::door::OverrideComponent")["note"] = "a person's note";
        using (Record.Lock(root)) Lead.Write(root, rows);

        var again = await new Builder(root, code, data, null).Build(Lead.Read(root));
        var door = Row(again, "esv::door::OverrideComponent");
        Assert.Equal("a person's note", (string?)door["note"]);
        Assert.Single(door["attempts"]!.AsArray());
        Assert.Equal("report", (string?)door["attempts"]![0]!["source"]);

        // Without the report and the note, a rebuild is the same text as the first.
        var clean = await new Builder(root, code, data, null).Build([]);
        using (Record.Lock(root)) Lead.Write(root, clean);
        Assert.Equal(first, File.ReadAllText(Lead.PathOf(root)));
    }

    [Fact]
    public async Task Next_hands_out_the_best_and_two_empties_park_a_lead_with_a_wish()
    {
        var root = Checkout();
        var (code, data) = Facts();
        using (Record.Lock(root)) Lead.Write(root, await new Builder(root, code, data, null).Build([]));

        var taken = Lead.Next(root, "world", null, 1);
        Assert.Equal("esv::door::OverrideComponent", (string?)taken.Single()["type"]);
        Assert.Equal("in_work", (string?)Row(Lead.Read(root), "esv::door::OverrideComponent")["state"]);
        Assert.DoesNotContain(Lead.Next(root, "coverage2", null, 5), r => (string?)r["type"] == "esv::door::OverrideComponent");

        var (row, wish) = Lead.Report(root, "esv::door::OverrideComponent", "empty", "nothing", null, null, null);
        Assert.Null(wish);
        Assert.Equal("open", (string?)row["state"]);
        Assert.Equal("world", (string?)row["attempts"]![0]!["instance"]);   // from the claim
        (row, wish) = Lead.Report(root, "esv::door::OverrideComponent", "empty", "nothing again", "world", null, null);
        Assert.Equal("parked", (string?)row["state"]);
        Assert.NotNull(wish);
        Assert.Contains("esv::door::OverrideComponent", (string?)Record.Read(root, "wishes").Single()["text"]);

        // A try with another instrument does not park; a type seen since is lit when read.
        Assert.Throws<LeadsException>(() => Lead.Report(root, "esv::door::OverrideComponent", "maybe", "?", null, null, null));
        _temp.Write("research/coverage/seen/index.json", """{"eoc::WeightBoostComponent": {"server": {"max": 1, "labels": []}}}""");
        Assert.Equal("lit", (string?)Row(Lead.Current(root), "eoc::WeightBoostComponent")["state"]);
        Assert.Equal(1, (int)Lead.Tally(Lead.Current(root))["by_state"]!["lit"]!);
    }

    [Fact]
    public void The_rules_read_names_as_the_table_says()
    {
        Assert.Equal(("boost", "solo"), Rules.ClassOf("eoc::DarkvisionRangeBoostComponent"));
        Assert.Equal(("split_screen_controller", "needs_pad"), Rules.ClassOf("eoc::controller::NudgeOneFrameComponent"));
        Assert.Equal(("cinematic", "solo"), Rules.ClassOf("ecl::timeline::PlayerTransitionComponent"));
        Assert.Equal(("lifecycle", "solo"), Rules.ClassOf("esv::GameStateLoadSessionComponent"));
        Assert.Equal(("editor_debug", "unknown"), Rules.ClassOf("ls::anubis::debug::DebuggingActivatedOneFrameComponent"));
        Assert.Equal(("gameplay_state", "solo"), Rules.ClassOf("esv::teleport::StateComponent"));

        Assert.Equal(("osiris", "SetTeleportTargetOverride"), Rules.EntryOf("esv::OsirisItemFunctions::SetTeleportTargetOverride"));
        Assert.Equal(("system_update", "esv::teleport::ExecutionSystem"), Rules.EntryOf("ecs::_private::SystemRegistrationHelper::SystemUpdate<esv::teleport::ExecutionSystem>"));
        Assert.Equal("functor", Rules.EntryOf("esv::ExecuteStatsFunctor_MoveContext")?.Kind);
        Assert.Equal("message_handler", Rules.EntryOf("ecl::CharacterProtocol::ProcessMsg")?.Kind);
        Assert.Equal("replication", Rules.EntryOf("ecs::sync::ComponentDeserializer<eoc::WeightBoostComponent>::DeserializeComponent")?.Kind);
        Assert.Null(Rules.EntryOf("FUN_143374150"));
        Assert.Equal("ls::anubis::System::Update", Rules.Demangle("__ZN2ls6anubis6System6UpdateERN3ecs9WorldViewIJ"));

        Assert.Equal("TargetOverride", Rules.Core("esv::teleport::HasTargetOverrideComponent"));
        Assert.Equal("Weight", Rules.BoostName("eoc::WeightBoostComponent"));
        Assert.Equal("ImprovisedWeapon", Rules.Pascal("esv::improvised_weapon::WieldedComponent"));
    }

    [Fact]
    public void The_runs_prose_counts_a_try_only_where_a_sentence_says_one_failed()
    {
        var unseen = new[] { "esv::lock::NeedsValidationComponent", "esv::lock::OtherOneFrameComponent", "esv::teleport::SpellComponent" };
        var tries = History.Read("Lockpicking worked. esv::lock::{NeedsValidation} never seen. Next: esv::teleport through a waypoint. "
            + "Osi.TeleportTo moved Astarion, no esv::teleport type.", "2026-11-14T03:00", "r", "world", "runs.jsonl", unseen).ToList();
        Assert.Equal(2, tries.Count);
        Assert.Equal("esv::lock::NeedsValidationComponent", tries[0].Type);
        Assert.Equal(("esv::teleport::SpellComponent", "osiris"), (tries[1].Type, tries[1].Attempt.Instrument));
    }
}
