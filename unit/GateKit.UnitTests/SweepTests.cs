using System.Text.Json.Nodes;
using GateKit.Copies;
using GateKit.Sweep;

namespace GateKit.UnitTests;

/// <summary>`gatekit sweep`: the stats languages, plans built from data, and a run against
/// FakeBridge - steps, what they lit, a hang retried once and then skipped, resuming, recipes.</summary>
public sealed class SweepTests : IAsyncLifetime
{
    readonly FakeGame _game = new();
    readonly List<FakeBridge> _bridges = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var bridge in _bridges) await bridge.DisposeAsync();
        _game.Dispose();
    }

    // --- the stats languages ---

    [Fact]
    public void Boost_lists_are_read_with_conditions_prefixes_and_nested_parentheses()
    {
        var calls = StatsText.Calls("AC(1);IF(HasStatus('DRUNK;X',context.Target)):RollBonus(Attack,WisdomModifier);  ;"
            + "UnlockSpellVariant(IsPerformSpell(),ModifyIconGlow());BlockTravel;IF(Tagged('PLAYABLE'))");
        Assert.Equal(["AC", "RollBonus", "UnlockSpellVariant", "BlockTravel"], calls.Select(c => c.Name));
        Assert.False(calls[0].Conditional);
        Assert.True(calls[1].Conditional);
        Assert.Equal("RollBonus(Attack,WisdomModifier)", calls[1].Text);
        Assert.Equal("IsPerformSpell(),ModifyIconGlow()", calls[2].Arguments);
        Assert.Equal("BlockTravel", calls[3].Text);
    }

    [Fact]
    public void Functor_lists_keep_their_target_prefixes()
    {
        var calls = StatsText.Calls("TARGET:IF(not Ally()):DealDamage(1d6,Fire);GROUND:CreateSurface(2,,Fire);AI_ONLY:ApplyStatus(X,100,1)");
        Assert.Equal(["DealDamage", "CreateSurface", "ApplyStatus"], calls.Select(c => c.Name));
        Assert.Equal(["TARGET"], calls[0].Prefixes);
        Assert.True(calls[0].Conditional);
        Assert.Equal(["AI_ONLY"], calls[2].Prefixes);
        Assert.Equal(["DisableOverhead", "IgnoreResting"], StatsText.Flags("DisableOverhead;IgnoreResting;"));
    }

    // --- plans ---

    static readonly string[] Types =
    [
        "eoc::ArmorClassBoostComponent", "eoc::ActionResourceValueBoostComponent", "eoc::ActionResourceBlockBoostComponent",
        "eoc::IgnoreDamageThresholdMinBoostComponent", "eoc::ReceivingCriticalDamageOnHitBoostComponent", "eoc::HorizontalFOVOverrideBoostComponent",
        "eoc::SavantBoostComponent", "eoc::ACOverrideFormulaBoostComponent", "eoc::BoostInfoComponent", "esv::status::StatusComponent",
    ];

    static StatEntry Stat(string name, string type, params (string Field, string Value)[] fields) =>
        new(name, type, null, "Shared", fields.ToDictionary(f => f.Field, f => f.Value));

    static StatsData Stats() => new(
    [
        Stat("ENTHRALL", "StatusData", ("StatusType", "BOOST"), ("Boosts", "HorizontalFOVOverride(10)")),
        Stat("ENTHRALL_TEST", "StatusData", ("StatusType", "BOOST"), ("Boosts", "HorizontalFOVOverride(5)")),
        Stat("Passive_Fov", "PassiveData", ("Boosts", "IF(InCombat()):HorizontalFOVOverride(20)")),
        Stat("Passive_Resource", "PassiveData", ("Boosts", "ActionResource(Rage,2,0);ActionResourceBlock(KiPoint)")),
        Stat("WARD", "StatusData", ("StatusType", "INCAPACITATED"), ("Boosts", "IgnoreDamageThreshold(All,10)"),
            ("StatusPropertyFlags", "DisableOverhead;IgnoreResting"), ("OnApplyFunctors", "ApplyStatus(SELF,BURNING,100,1)")),
        new("WARD_CHILD", "StatusData", "WARD", "Shared", new Dictionary<string, string> { ["Boosts"] = "CriticalDamageOnHit()" }),
        Stat("Target_Bolt", "SpellData", ("SpellType", "Target"), ("SpellSuccess", "DealDamage(1d10,Fire)"), ("SpellFlags", "IsAttack;IsSpell")),
        Stat("Zone_Bolt", "SpellData", ("SpellType", "Zone"), ("SpellSuccess", "DealDamage(1d10,Fire)"), ("SpellFlags", "IsSpell")),
    ]);

    [Fact]
    public void A_boost_lands_on_its_component_by_the_registered_names_alone()
    {
        var stems = PlanBuilder.BoostStems(Types);
        var names = new HashSet<string>(["AC", "ActionResource", "ActionResourceBlock", "IgnoreDamageThreshold", "CriticalDamageOnHit", "ACOverrideFormula"]);
        Assert.Equal("eoc::ActionResourceValueBoostComponent", PlanBuilder.BoostComponentOf("ActionResource", stems, names));
        Assert.Equal("eoc::ActionResourceBlockBoostComponent", PlanBuilder.BoostComponentOf("ActionResourceBlock", stems, names));
        Assert.Equal("eoc::IgnoreDamageThresholdMinBoostComponent", PlanBuilder.BoostComponentOf("IgnoreDamageThreshold", stems, names));
        Assert.Equal("eoc::ReceivingCriticalDamageOnHitBoostComponent", PlanBuilder.BoostComponentOf("CriticalDamageOnHit", stems, names));
        // AC is not a prefix of ArmorClass, and ACOverrideFormula is a boost of its own: nothing is guessed
        Assert.Null(PlanBuilder.BoostComponentOf("AC", stems, names));
        Assert.False(stems.ContainsKey("BoostInfo"));
    }

    [Fact]
    public void The_boosts_plan_takes_the_cheapest_shipping_carrier_and_a_direct_fallback()
    {
        var plan = PlanBuilder.Build("boosts", Stats(), Types, "2026-11-12");
        var fov = plan.Steps.Single(s => s.Id == "boost:HorizontalFOVOverride");
        // the passive is conditional, the test status is not shipping: ENTHRALL wins
        Assert.Equal(new Carrier("status", "ENTHRALL", "Boosts", true), fov.Carrier);
        Assert.Equal(["eoc::HorizontalFOVOverrideBoostComponent"], fov.Expects!);
        Assert.Equal("cheat", fov.ReachedBy);
        Assert.Contains("Osi.ApplyStatus(D, \"ENTHRALL\"", fov.How);
        Assert.Equal("direct", fov.Fallback!.Carrier.Kind);
        Assert.Contains("HorizontalFOVOverride(10)", fov.Fallback.How);
        // inherited fields: the child carries its own boost and its parent's flags
        Assert.Equal("WARD_CHILD", plan.Steps.Single(s => s.Id == "boost:CriticalDamageOnHit").Carrier.Name);
        Assert.True(plan.Steps.Single(s => s.Id == "boost:IgnoreDamageThreshold").Fresh);   // functors on apply, not a BOOST
        // a registered boost component no entry names is reached only directly, labelled so
        var direct = plan.Steps.Single(s => s.Id == "boost-direct:Savant");
        Assert.Equal("direct", direct.Carrier.Kind);
        Assert.False(direct.Carrier.Shipping);
        Assert.Contains("Osi.AddBoosts(D, \"Savant()\"", direct.How);
        // the same data gives the same plan, byte for byte
        Assert.Equal(plan.ToJson(), PlanBuilder.Build("boosts", Stats(), Types.Reverse().ToArray(), "2026-11-12").ToJson());
    }

    [Fact]
    public void Statuses_functors_and_spell_flags_each_get_one_step_per_thing()
    {
        var statuses = PlanBuilder.Build("statuses", Stats(), Types);
        Assert.Equal(["status-type:BOOST", "status-type:INCAPACITATED", "status-flag:DisableOverhead", "status-flag:IgnoreResting"],
            statuses.Steps.Select(s => s.Id));
        Assert.Equal("ENTHRALL", statuses.Steps[0].Carrier.Name);

        var functors = PlanBuilder.Build("functors", Stats(), Types);
        Assert.Equal(["functor:ApplyStatus", "functor:DealDamage"], functors.Steps.Select(s => s.Id));
        var damage = functors.Steps[1];
        Assert.Equal("Target_Bolt", damage.Carrier.Name);          // a target spell before a zone
        Assert.Contains("GK.Stage.Cast(\"Target_Bolt\", T, D)", damage.How);

        var flags = PlanBuilder.Build("spellflags", Stats(), Types);
        Assert.Equal(["spell-flag:IsAttack", "spell-flag:IsSpell"], flags.Steps.Select(s => s.Id));
        Assert.Throws<SweepException>(() => PlanBuilder.Build("nonsense", Stats(), Types));
    }

    [Fact]
    public void A_plan_survives_its_file()
    {
        var plan = PlanBuilder.Build("boosts", Stats(), Types, "x");
        var path = _game.Temp.Write("plan.json", plan.ToJson());
        Assert.Equal(plan.ToJson(), SweepPlan.Read(path).ToJson());
    }

    // --- a run against FakeBridge ---

    /// <summary>A copy whose Lua is FakeBridge and whose capture tools are faked here: a status
    /// lights a type the first time it is applied, one step's Lua never answers. The sweep runs on
    /// <see cref="Clock"/>: its own waits move it, and so does the hung step, past any limit.</summary>
    sealed class BridgeGame : ISweepGame
    {
        readonly Copies.GameCopy _copy;
        public readonly ManualClock Clock = new();
        public SweepTime Time => new(Clock, Clock.Wait);
        public readonly List<string> Chunks = [];
        public readonly List<string> Tools = [];
        public readonly Queue<JsonObject> Fresh = new();
        public string Label = "";
        public int Restarts;

        public BridgeGame(SweepTests tests, string root)
        {
            CoverageRoot = root;
            _copy = tests._game.GameCopy("world");
            tests._game.Running(_copy);
            foreach (var side in new[] { "client", "server" })
                tests._bridges.Add(new FakeBridge(_copy.Layout.Channel, side, code => Answer(code[GateKit.Bridge.BridgeClient.Prelude.Length..])));
        }

        JsonNode? Answer(string code)
        {
            lock (Chunks) Chunks.Add(code);
            if (code.Contains("GK.Seen.Mark(", StringComparison.Ordinal))
            {
                Label = code[(code.IndexOf('"') + 1)..code.LastIndexOf('"')];
                return true;
            }
            if (code.Contains("SweepDummy = GK.Stage.Spawn", StringComparison.Ordinal)) return new JsonObject { ["D"] = "d1", ["ready"] = true };
            if (code.Contains("GetComponentCounts", StringComparison.Ordinal)) return new JsonObject();
            if (code.Contains("\"HANG\"", StringComparison.Ordinal))
            {
                Clock.Advance(TimeSpan.FromHours(1));
                throw new FakeBridge.NoAnswer();
            }
            if (code.Contains("\"LIGHT\"", StringComparison.Ordinal) && code.Contains("ApplyStatus", StringComparison.Ordinal))
                Fresh.Enqueue(new JsonObject { ["Name"] = "eoc::NeverSeenBoostComponent", ["Label"] = Label, ["Max"] = 1, ["side"] = "server" });
            if (code.Contains("\"BROKEN\"", StringComparison.Ordinal)) throw new InvalidOperationException("attempt to call a nil value");
            if (code.Contains("CHECKFAIL", StringComparison.Ordinal)) return "it did not happen";
            if (code.Contains("CHECKNOTE", StringComparison.Ordinal)) return new JsonObject { ["ok"] = true, ["note"] = "under the dummy: SurfaceFire" };
            return true;
        }

        public string Level => "GustavX:Basic_Level_A";
        public string CoverageRoot { get; }
        public GameSession? Session => null;
        public Copies.GameCopy? GameCopy => null;

        public Task<JsonNode?> LuaAsync(string side, string code, TimeSpan timeout, CancellationToken cancel) =>
            _copy.Session().LuaAsync(side, code, timeout, cancel);

        public Task<JsonNode?> ToolAsync(string name, JsonObject arguments, CancellationToken cancel)
        {
            lock (Tools) Tools.Add(name);
            JsonNode? answer = name switch
            {
                "bg3_seen_new" => new JsonObject { ["new_to_the_record"] = new JsonArray([.. Fresh.Select(r => (JsonNode)r.DeepClone())]) },
                _ => new JsonObject(),
            };
            if (name == "bg3_seen_new") Fresh.Clear();
            return Task.FromResult<JsonNode?>(answer);
        }

        public bool Alive() => true;
        public Task EnsureUpAsync(CancellationToken cancel) => Task.CompletedTask;
        public Task RestartAsync(CancellationToken cancel) { Restarts++; return Task.CompletedTask; }
        public Task CheckpointAsync(CancellationToken cancel) => Task.CompletedTask;
        public Task ResetWorldAsync(CancellationToken cancel) => Task.CompletedTask;
    }

    static SweepStep Status(string id, string status, bool fresh = false) => new()
    {
        Id = id, What = "status " + status, Carrier = new Carrier("status", status, "Boosts"), ReachedBy = "cheat",
        How = $"Osi.ApplyStatus(D, \"{status}\", -1.0, 1, D)", Undo = $"pcall(Osi.RemoveStatus, D, \"{status}\")", Settle = 0.1, Fresh = fresh,
    };

    [Fact]
    public async Task A_run_records_every_step_lights_types_skips_a_hang_and_resumes()
    {
        var root = _game.Temp["research/coverage"];
        Directory.CreateDirectory(Path.Combine(root, "record"));
        File.WriteAllText(Path.Combine(root, "coverage.json"), "{\"components\": []}");
        var game = new BridgeGame(this, root);
        var plan = new SweepPlan { Kind = "boosts", Steps = [Status("s:one", "PLAIN"), Status("s:lit", "LIGHT"), Status("s:hang", "HANG"), Status("s:bad", "BROKEN"), Status("s:last", "PLAIN")] };
        var planPath = _game.Temp.Write("sweep/boosts.json", plan.ToJson());
        var statePath = Path.ChangeExtension(planPath, ".state.json");
        var output = new StringWriter();
        var options = new SweepOptions { Run = "test-run", StepTimeout = 1, DrainEvery = 2 };

        var state = await new SweepRunner(game, output, game.Time).RunAsync(plan, planPath, statePath, options with { Limit = 4 }, CancellationToken.None);

        Assert.Equal(4, state.Next);
        Assert.Equal(["s:one", "s:lit", "s:hang", "s:bad"], state.Steps.Select(s => s.Id));
        var lit = Assert.Single(state.Lit);
        Assert.Equal(new LitType("eoc::NeverSeenBoostComponent", "server", "s:lit", "carrier"), lit);
        Assert.Contains("s:hang", state.Skipped);
        Assert.Equal(2, state.Hangs["s:hang"]);
        Assert.False(state.Steps.Single(s => s.Id == "s:bad").Ok);
        Assert.Contains("nil value", state.Steps.Single(s => s.Id == "s:bad").Error);
        Assert.True(state.Steps.Single(s => s.Id == "s:one").Ok);
        Assert.Contains("bg3_seen_start", game.Tools);
        Assert.Contains("bg3_seen_drain", game.Tools);
        // every step was labelled before it ran, and undone after
        Assert.Contains(game.Chunks, c => c.Contains("GK.Seen.Mark(\"sweep:boosts:s:lit\")", StringComparison.Ordinal));
        Assert.Contains(game.Chunks, c => c.Contains("pcall(Osi.RemoveStatus, D, \"LIGHT\")", StringComparison.Ordinal));

        // a recipe for the type it lit, not replayed without a game, and a findings row that says so
        var recipe = Assert.Single(state.Recipes);
        Assert.False(recipe.Confirmed);
        var test = File.ReadAllText(_game.Temp["tests/_coverage/" + SweepRecipes.FileName("boosts", "s:lit")]);
        Assert.Contains("watch(\"server\", \"eoc::NeverSeenBoostComponent\")", test);
        Assert.Contains("alive(\"server\", \"eoc::NeverSeenBoostComponent\", 10)", test);
        Assert.Contains("Osi.ApplyStatus(D, \"LIGHT\"", test);
        Assert.Contains("GK.Stage.Remove", test);
        var finding = JsonNode.Parse(File.ReadAllLines(Path.Combine(root, "findings.jsonl")).Single())!;
        Assert.Equal("lead", finding["kind"]!.GetValue<string>());
        Assert.Equal("cheat", finding["reached_by"]!.GetValue<string>());
        Assert.Equal("sighting", finding["evidence"]!.GetValue<string>());

        // resumed from the state file: only the last step runs
        var again = await new SweepRunner(game, output, game.Time).RunAsync(plan, planPath, statePath, options, CancellationToken.None);
        Assert.Equal(5, again.Next);
        Assert.Equal("s:last", again.Steps.Last().Id);
        Assert.Equal(5, again.Steps.Count);
    }

    [Fact]
    public async Task A_settle_longer_than_the_step_timeout_is_not_a_hang()
    {
        var root = _game.Temp["research/coverage"];
        Directory.CreateDirectory(Path.Combine(root, "record"));
        File.WriteAllText(Path.Combine(root, "coverage.json"), "{\"components\": []}");
        var game = new BridgeGame(this, root);
        var plan = new SweepPlan { Kind = "boosts", Steps = [Status("s:slow", "PLAIN") with { Settle = 30 }] };
        var planPath = _game.Temp.Write("sweep/slow.json", plan.ToJson());

        var state = await new SweepRunner(game, new StringWriter(), game.Time).RunAsync(plan, planPath, Path.ChangeExtension(planPath, ".state.json"),
            new SweepOptions { Run = "test-run", StepTimeout = 1, Recipes = false }, CancellationToken.None);

        var step = Assert.Single(state.Steps);
        Assert.True(step.Ok, step.Error);
        Assert.Empty(state.Hangs);
        Assert.True(step.Seconds >= 30);   // the settle passed on the step's clock and was not held against it
    }

    [Fact]
    public void A_recipe_of_several_types_watches_and_checks_each_and_cleans_up()
    {
        var step = Status("boost:Two", "TWO");
        var plan = new SweepPlan { Kind = "boosts", Steps = [step] };
        var text = SweepRecipes.RecipeText(plan, step, [("server", "eoc::ABoostComponent"), ("client", "eoc::BBoostComponent")], false, "GustavX:Basic_Level_A");
        var path = _game.Temp.Write("tests/sweep_two.lua", text);
        var test = GateKit.Testing.GameTest.Load(path);
        Assert.Equal("GustavX:Basic_Level_A", test.Start.Level);
        Assert.Contains("watch(\"client\", \"eoc::BBoostComponent\")", text);
        Assert.Contains("seen = alive(\"client\", \"eoc::BBoostComponent\", 1)", text);
        Assert.True(text.IndexOf("alive(\"client\"", StringComparison.Ordinal) < text.IndexOf("GK.Stage.Remove", StringComparison.Ordinal));
        Assert.Contains("Reached by cheat", text);
    }

    [Fact]
    public void A_label_names_its_step()
    {
        Assert.Equal("boost:AC", SweepRunner.StepOf("boosts", SweepRunner.Label("boosts", "boost:AC")));
        Assert.Equal("boost:AC", SweepRunner.StepOf("boosts", "sweep:boosts:boost:AC:direct"));
        Assert.Null(SweepRunner.StepOf("boosts", "sweep:boosts:arm"));
        Assert.Null(SweepRunner.StepOf("boosts", "idle"));
    }

    // --- the second sweep: leads, enums, checks ---

    static JsonObject LeadRow(string type, string instrument, string how) => new()
    {
        ["type"] = type, ["side"] = "server", ["class"] = "gameplay_state", ["reachable"] = "solo", ["entry"] = new JsonObject { ["kind"] = "osiris" },
        ["data"] = new JsonArray(), ["how"] = how, ["instrument"] = instrument, ["score"] = 10, ["state"] = "open", ["attempts"] = new JsonArray(),
    };

    [Fact]
    public void The_leads_plan_runs_only_concrete_hows_and_shares_one_step_between_leads()
    {
        var rows = new List<JsonObject>
        {
            LeadRow("eoc::HorizontalFOVOverrideBoostComponent", "osiris", "Give a character ENTHRALL (a status: Osi.ApplyStatus), which carries HorizontalFOVOverride(...) in Boosts, with a capture running."),
            LeadRow("eoc::SightRangeAdditiveBoostComponent", "stage", "Nothing in the game's data carries the boost SightRangeAdditive(): only a script adding it (Osi.AddBoosts, a cheat) or a mod would."),
            LeadRow("esv::teleport::HasTargetOverrideComponent", "osiris", "Try Osi.SetTeleportTargetOverride (matched by name), then play while X runs."),
            LeadRow("esv::teleport::IsTargetOverrideComponent", "osiris", "Try Osi.SetTeleportTargetOverride (matched by name), then play while X runs."),
            LeadRow("esv::unknown::ThingComponent", "osiris", "Try Osi.NoSuchCall (matched by name), then play."),
            LeadRow("ecl::ModuleLoadComponent", "sweep", "No entry is known: catch it with a wide sweep around ecl."),
            LeadRow("eoc::PlayOnlyComponent", "play", "Give a character ENTHRALL (a status: Osi.ApplyStatus)."),
        };
        var plan = PlanBuilder.Build("leads", Stats(), Types, "x", new PlanInputs { Leads = rows });
        Assert.Equal(["lead-osi:NoSuchCall", "lead-boost:SightRangeAdditive", "lead-osi:SetTeleportTargetOverride", "lead:ENTHRALL"],
            plan.Steps.Select(s => s.Id).Order(StringComparer.Ordinal).OrderBy(id => id == "lead-osi:NoSuchCall" ? 0 : 1));
        Assert.Equal(1, plan.Counts!["leads_too_vague_to_run"]!.GetValue<int>());
        var status = plan.Steps.Single(s => s.Id == "lead:ENTHRALL");
        Assert.Contains("Osi.ApplyStatus(D, \"ENTHRALL\"", status.How);
        Assert.Equal("osiris", status.Instrument);
        Assert.Equal(["eoc::HorizontalFOVOverrideBoostComponent"], status.Leads!);
        var boost = plan.Steps.Single(s => s.Id == "lead-boost:SightRangeAdditive");
        Assert.Contains("SightRangeAdditive(Attack,10)", boost.How);
        Assert.Equal(SweepLeads.BoostShapes.Count, boost.How.Split('\n').Length);
        var teleport = plan.Steps.Single(s => s.Id == "lead-osi:SetTeleportTargetOverride");
        Assert.Equal(2, teleport.Leads!.Count);
        Assert.Contains("Osi.SetTeleportTargetOverride(SweepItem, D)", teleport.How);   // never item to item
        Assert.Equal("cheat", teleport.ReachedBy);
        Assert.NotNull(plan.Steps.Single(s => s.Id == "lead-osi:NoSuchCall").Blocked);
    }

    [Fact]
    public void Enum_plans_need_the_running_games_enums_and_skip_what_the_data_has()
    {
        Assert.Throws<SweepException>(() => PlanBuilder.Build("statustypes", Stats(), Types));
        var inputs = new PlanInputs
        {
            Enums = new Dictionary<string, IReadOnlyList<string>>
            {
                ["StatusType"] = ["BOOST", "INCAPACITATED", "ROTATE", "UNLOCK"],
                ["SurfaceType"] = ["None", "Fire", "Water"],
            },
        };
        var statuses = PlanBuilder.Build("statustypes", Stats(), Types, null, inputs);
        Assert.Equal(["status-enum:ROTATE", "status-enum:UNLOCK"], statuses.Steps.Select(s => s.Id));
        Assert.Contains("Osi.HasActiveStatus(D, \"ROTATE\")", statuses.Steps[0].Check);
        var surfaces = PlanBuilder.Build("surfaces", Stats(), Types, null, inputs);
        Assert.Equal(["surface:Fire", "surface:Water"], surfaces.Steps.Select(s => s.Id));
        Assert.Contains("\"SurfaceFire\"", surfaces.Steps[0].How);
        // each surface on its own spot: blood left by one dummy is never the next one's surface
        Assert.Contains("GK.Stage.Position(H), 0 *", surfaces.Steps[0].How);
        Assert.Contains("GK.Stage.Position(H), 1 *", surfaces.Steps[1].How);
        Assert.Contains("ground == wanted or cloud == wanted", surfaces.Steps[0].Check);
    }

    [Fact]
    public void Contexts_spells_interrupts_items_and_summons_are_built_from_the_data()
    {
        var stats = new StatsData(
        [
            Stat("Passive_Hit", "PassiveData", ("StatsFunctorContext", "OnAttack;OnLongRest"), ("StatsFunctors", "ApplyStatus(BLESS,100,1)")),
            Stat("Target_Bolt", "SpellData", ("SpellType", "Target"), ("Level", "1"), ("SpellSuccess", "Summon(abc-def,Permanent,Projectile_AiHelper_Summon_Strong)")),
            Stat("Shout_Cry", "SpellData", ("SpellType", "Shout"), ("HitAnimationType", "MagicalNonDamage")),
            Stat("Interrupt_Shield", "InterruptData", ("InterruptContext", "OnPreDamage")),
            Stat("Interrupt_Odd", "InterruptData", ("InterruptContext", "OnSomethingNew")),
            Stat("ARM_Ring", "Armor", ("RootTemplate", "12345678-1234-1234-1234-123456789012"), ("PassivesOnEquip", "Passive_Hit"), ("DefaultBoosts", "AC(1)")),
        ]);
        var contexts = PlanBuilder.Build("contexts", stats, Types);
        Assert.Equal(["context:OnAttack", "context:OnLongRest"], contexts.Steps.Select(s => s.Id));
        Assert.Contains("GK.Stage.Cast, \"Target_MainHandAttack\", T, D", contexts.Steps[0].How);
        Assert.Null(contexts.Steps[0].Blocked);
        Assert.NotNull(contexts.Steps[1].Blocked);

        var spells = PlanBuilder.Build("spelltypes", stats, Types);
        var shout = spells.Steps.Single(s => s.Id == "cast:SpellType:Shout");
        Assert.Equal("play_with_cheat_setup", shout.ReachedBy);
        Assert.Contains("GK.Cheat.Cast(H, \"Shout_Cry\", H", shout.How);
        Assert.Contains("v.status == 'done'", shout.Check);
        Assert.Contains(spells.Steps, s => s.Id == "cast:HitAnimationType:MagicalNonDamage");

        var interrupts = PlanBuilder.Build("interrupts", stats, Types);
        Assert.Contains("UnlockInterrupt(Interrupt_Shield)", interrupts.Steps.Single(s => s.Id == "interrupt:OnPreDamage").How);
        Assert.NotNull(interrupts.Steps.Single(s => s.Id == "interrupt:OnSomethingNew").Blocked);

        var items = PlanBuilder.Build("items", stats, Types);
        Assert.Equal(["item:boost:AC", "item:context:OnAttack", "item:context:OnLongRest"], items.Steps.Select(s => s.Id));
        Assert.All(items.Steps, s => Assert.Contains("Osi.Equip(D", s.How));
        Assert.All(items.Steps, s => Assert.Contains("Osi.IsEquipped(SweepItem)", s.Check));

        var summons = PlanBuilder.Build("summons", stats, Types);
        Assert.Equal("summon:Summon/3/Permanent+helper", Assert.Single(summons.Steps).Id);
    }

    [Fact]
    public async Task A_run_checks_its_steps_skips_the_blocked_and_reports_its_leads()
    {
        var root = _game.Temp["research/coverage"];
        Directory.CreateDirectory(Path.Combine(root, "record"));
        Directory.CreateDirectory(Path.Combine(root, "seen"));
        File.WriteAllText(Path.Combine(root, "coverage.json"), "{\"components\": []}");
        File.WriteAllText(Path.Combine(root, "seen", "index.json"), "{\"eoc::LitBoostComponent\": {\"server\": {\"max\": 1}}}");
        GateKit.Leads.Lead.Write(root, [LeadRow("eoc::LitBoostComponent", "stage", "x"), LeadRow("eoc::EmptyBoostComponent", "osiris", "y"),
            LeadRow("eoc::BlockedComponent", "osiris", "z")]);
        var game = new BridgeGame(this, root);
        var plan = new SweepPlan
        {
            Kind = "leads",
            Steps =
            [
                Status("s:lit", "PLAIN") with { Leads = ["eoc::LitBoostComponent"], Instrument = "stage", Check = "return 'CHECKNOTE'" },
                Status("s:empty", "PLAIN") with { Leads = ["eoc::EmptyBoostComponent"], Instrument = "osiris", Check = "return 'CHECKFAIL'" },
                Status("s:blocked", "NEVER") with { Leads = ["eoc::BlockedComponent"], Blocked = "no safe arguments" },
            ],
        };
        var planPath = _game.Temp.Write("sweep/leads.json", plan.ToJson());
        var state = await new SweepRunner(game, new StringWriter(), game.Time).RunAsync(plan, planPath, Path.ChangeExtension(planPath, ".state.json"),
            new SweepOptions { Run = "test-run", StepTimeout = 5, Recipes = false, Instance = "world" }, CancellationToken.None);

        Assert.True(state.Steps.Single(s => s.Id == "s:lit").Checked);
        Assert.Equal("under the dummy: SurfaceFire", state.Steps.Single(s => s.Id == "s:lit").CheckNote);
        Assert.False(state.Steps.Single(s => s.Id == "s:empty").Checked);
        Assert.Equal("it did not happen", state.Steps.Single(s => s.Id == "s:empty").CheckNote);
        Assert.True(state.Steps.Single(s => s.Id == "s:blocked").Blocked);
        Assert.DoesNotContain(game.Chunks, c => c.Contains("NEVER", StringComparison.Ordinal));
        Assert.Equal(3, state.Reported.Count);
        var rows = GateKit.Leads.Lead.Read(root).ToDictionary(r => r["type"]!.GetValue<string>());
        string Outcome(string type) => rows[type]["attempts"]!.AsArray().Last()!["outcome"]!.GetValue<string>();
        Assert.Equal("lit", Outcome("eoc::LitBoostComponent"));
        Assert.Equal("empty", Outcome("eoc::EmptyBoostComponent"));
        Assert.Equal("blocked", Outcome("eoc::BlockedComponent"));
        Assert.Equal(1, state.Summary()["checks_passed"]!.GetValue<int>());
    }

    [Fact]
    public void Tours_joined_by_commas_play_as_one_with_their_names_in_the_labels()
    {
        var a = _game.Temp.Write("tours/a.json", "[{\"label\": \"one\", \"server\": \"return 1\"}]");
        var b = _game.Temp.Write("tours/b.json", "[{\"label\": \"two\"}, {\"server\": \"return 2\"}]");
        var steps = SweepTour.Steps([a, b]);
        Assert.Equal(["a-one", "b-two", "b-1"], steps.Select(s => s!["label"]!.GetValue<string>()));
        Assert.Equal("one", SweepTour.Steps([a])[0]!["label"]!.GetValue<string>());
    }
}
