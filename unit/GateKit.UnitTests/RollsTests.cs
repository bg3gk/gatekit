using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.UnitTests;

/// <summary>
/// GK.Rolls and GK.Speed, run in a Lua interpreter with the bridge's real event log, stage ledger
/// and fixtures against a stand-in of the game: creatures with a boosts container that takes boosts
/// a few ticks after Osi.AddBoosts and lets them go a few ticks after Osi.RemoveBoosts (as the
/// game does), the roll kinds of StatsRollType, finished-roll events sent by hand.
/// </summary>
public sealed class RollsTests
{
    const string World = """
        World = { ticks = 0, later = {}, tickers = {}, deferred = {}, files = {}, creatures = {}, switches = { WeightedRolls = false } }
        local LAG = 3
        function World.after(n, f) World.later[#World.later + 1] = { at = World.ticks + n, f = f } end
        function World.tick(n)
            for _ = 1, n or 1 do
                World.ticks = World.ticks + 1
                local due, keep = {}, {}
                for _, l in ipairs(World.later) do if l.at <= World.ticks then due[#due + 1] = l else keep[#keep + 1] = l end end
                World.later = keep
                for _, l in ipairs(due) do l.f() end
                GK.EventLog.Tick()
                for _, f in ipairs(World.tickers) do f({ Time = { Ticks = World.ticks, Time = World.ticks / 30, DeltaTime = 1 / 30 } }) end
            end
        end
        function World.creature(id)
            local c = { Uuid = { EntityUuid = id }, BoostsContainer = { Boosts = {} } }
            World.creatures[id] = c
            return c
        end
        local function group(c, name)
            for _, g in ipairs(c.BoostsContainer.Boosts) do if g.Type == name then return g end end
            local g = { Type = name, Boosts = {} }
            c.BoostsContainer.Boosts[#c.BoostsContainer.Boosts + 1] = g
            return g
        end
        local function parts(text)
            local out = {}
            for part in text:gmatch("[^;]+") do
                local name, args = part:match("^(%w+)%((.*)%)$")
                out[#out + 1] = { name = name or part, args = args or "" }
            end
            return out
        end
        function World.boosts(id)
            local out = {}
            for _, g in ipairs(World.creatures[id].BoostsContainer.Boosts) do
                for _, b in ipairs(g.Boosts) do out[#out + 1] = b.BoostInfo.Cause.Cause .. ":" .. b.BoostInfo.Params.Boost .. "(" .. b.BoostInfo.Params.Params .. ")" end
            end
            table.sort(out)
            return table.concat(out, " ")
        end
        -- one finished roll of the roll system, as esv::roll::RollFinishedEventOneFrameComponent
        function World.finish(who, kind, natural, bonus)
            local ev = { Roller = who, Subject = who, RollType = Ext.Enums.StatsRollType[kind], Ability = "Wisdom", Skill = "Perception",
                         DC = 15, NaturalRoll = natural, DiceAdditionalValue = bonus or 3, RollContext = 8, Advantage = false, Disadvantage = false }
            for _, f in ipairs(World.deferred.ServerRollFinishedEvent or {}) do f(nil, nil, { Events = { ev } }) end
        end

        local kinds = { "Attack", "MeleeWeaponAttack", "SkillCheck", "SavingThrow", "RawAbility", "Damage", "DeathSavingThrow", "MeleeWeaponDamage" }
        local enum = { None = "None", Sentinel = "Sentinel", [0] = "None" }
        for i, name in ipairs(kinds) do enum[name] = name; enum[i] = name end

        Ext = {
            IsServer = function() return true end,
            Enums = { StatsRollType = enum },
            Utils = { GetGameState = function() return "Running" end, MonotonicTime = function() return World.ticks * 33 end,
                      GetGlobalSwitches = function() return World.switches end, PrintError = function() end, PrintWarning = function() end },
            Timer = { MicrosecTime = function() return World.ticks * 33000 end, WaitFor = function() end },
            IO = { LoadFile = function(path) if path:find("EventKinds", 1, true) then return REGISTRY end return World.files[path] end,
                   SaveFile = function(path, text) World.files[path] = text end },
            Json = { Parse = function(text) return json.parse(text) end, Stringify = function(value) return json.serialize(value) end },
            Events = { GameStateChanged = { Subscribe = function() end },
                       Tick = { Subscribe = function(_, f) World.tickers[#World.tickers + 1] = f end } },
            Entity = {
                Get = function(id) return World.creatures[id] end,
                OnCreateDeferred = function(name, f) World.deferred[name] = World.deferred[name] or {} table.insert(World.deferred[name], f) end,
                OnCreate = function() end, OnDestroy = function() end,
                GetAllEntitiesWithComponent = function(name)
                    local out = {}
                    for _, c in pairs(World.creatures) do if c[name] ~= nil then out[#out + 1] = c end end
                    return out
                end,
            },
            Osiris = { RegisterListener = function() end },
            System = { ServerRollSave = { RollManager = setmetatable({}, { __index = function(_, k)
                if k == "RollModeType" then return World.switches.WeightedRolls and "WeightedRandom" or "Random" end end }) } },
            Debug = {}, Loca = {}, Net = {},
            StaticData = {
                GetAll = function(kind) return kind == "ActionResource" and { "g-action", "g-move", "g-insp" } or {} end,
                Get = function(guid) return ({ ["g-action"] = { Name = "ActionPoint" }, ["g-move"] = { Name = "Movement" },
                                               ["g-insp"] = { Name = "InspirationPoint", MaxValue = 4 } })[guid] end,
            },
        }
        Osi = {
            GetHostCharacter = function() return "Host_11111111-1111-1111-1111-111111111111" end,
            AddBoosts = function(who, text, cause)
                World.after(LAG, function()
                    for _, p in ipairs(parts(text)) do
                        local g = group(World.creatures[who], p.name)
                        g.Boosts[#g.Boosts + 1] = { BoostInfo = { Cause = { Type = "Story", Cause = cause }, Params = { Boost = p.name, Params = p.args } } }
                    end
                end)
            end,
            RemoveBoosts = function(who, text, _, cause)
                World.after(LAG + 1, function()
                    for _, p in ipairs(parts(text)) do
                        local g = group(World.creatures[who], p.name)
                        for i = #g.Boosts, 1, -1 do
                            local info = g.Boosts[i].BoostInfo
                            if info.Cause.Cause == cause and info.Params.Params == p.args then table.remove(g.Boosts, i) break end
                        end
                    end
                end)
            end,
        }

        -- what the file channel does with an observed answer: a look every tick until it is final
        function Settle(answer)
            local value, check, within = GK.Unobserved(answer)
            if not check then return value end
            for i = 1, 30 do
                local seen = check(i * 33, false)
                if seen then return seen end
                World.tick()
            end
            return check(within, true)
        end

        function Rows(kind)
            local out = {}
            for _, row in ipairs(GK.EventLog.Since(0, { kinds = { [kind] = true } }).rows) do out[#out + 1] = row end
            return out
        end
        """;

    const string Host = "11111111-1111-1111-1111-111111111111";
    const string Guard = "22222222-2222-2222-2222-222222222222";

    /// <summary>The server's Lua with the stage, fixtures, speed and rolls against the stand-in world (also StageHitPointsTests').</summary>
    internal static Script Game()
    {
        var lua = Path.Combine(CopySettings.RepositoryRoot()!, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua");
        var script = new Script(CoreModules.Preset_Complete);
        script.Globals["REGISTRY"] = File.ReadAllText(Path.Combine(lua, "Shared", "EventKinds.json"));
        script.DoString(World, null, "world");
        foreach (var file in new[] { "Shared/Bridge.lua", "Shared/EventLog.lua", "Server/Stage.lua", "Server/Fixtures.lua", "Server/Speed.lua", "Server/Rolls.lua" })
            script.DoString(File.ReadAllText(Path.Combine(lua, file)), null, file);
        script.DoString($"""
            World.creature("{Host}") World.creature("{Guard}")
            """);
        return script;
    }

    /// <summary>A stage scope as Begin leaves it, without the world census Begin takes.</summary>
    const string Scope = "GK.Stage.Ledger.baseline = { level = 'test' } GK.EventLog.Scope = GK.EventLog.Seq + 1 ";

    static DynValue Run(Script game, string code) => game.DoString(code);

    static string Error(Script game, string code) =>
        Assert.Throws<ScriptRuntimeException>(() => Run(game, code)).DecoratedMessage ?? "";

    [Fact]
    public void A_pin_outside_a_scope_an_unknown_kind_and_once_on_an_attack_are_refused_and_say_why()
    {
        var game = Game();
        Assert.Contains("GK.Stage.Begin() first", Error(game, "GK.Rolls.Pin{ kind = 'SkillCheck', n = 20 }"), StringComparison.Ordinal);
        Run(game, Scope);
        var unknown = Error(game, "GK.Rolls.Pin{ kind = 'Perception', n = 20 }");
        Assert.Contains("no roll kind Perception", unknown, StringComparison.Ordinal);
        Assert.Contains("SkillCheck", unknown, StringComparison.Ordinal);
        Assert.Contains("Initiative", unknown, StringComparison.Ordinal);
        Assert.Contains("once is for the kinds", Error(game, "GK.Rolls.Pin{ kind = 'MeleeWeaponAttack', n = 20, once = true }"), StringComparison.Ordinal);
        Assert.Contains("at most 20", Error(game, "GK.Rolls.Pin{ kind = 'SavingThrow', n = 21 }"), StringComparison.Ordinal);
        Assert.Contains("whole number", Error(game, "GK.Rolls.Pin{ kind = 'SavingThrow', n = 2.5 }"), StringComparison.Ordinal);
        Assert.Equal("", Run(game, $"return World.boosts('{Host}')").String);
    }

    [Fact]
    public void Refill_gives_the_turns_resources_back_and_its_undo_is_data_that_outlives_the_lua_state()
    {
        var game = Game();
        Run(game, Scope + $$"""
            local c = World.creatures["{{Host}}"]
            c.ActionResources = { Resources = { ["g-action"] = { { Amount = 0, MaxAmount = 1 } }, ["g-move"] = { { Amount = 1.5, MaxAmount = 9 } } } }
            """);
        var answer = Run(game, $"return Settle(GK.Stage.Refill{{ who = '{Host}' }})").Table;
        Assert.True(answer.Get("ok").Boolean);
        Assert.Equal(2, answer.Get("changed").Table.Length);
        Assert.Equal(1, Run(game, $"return World.creatures['{Host}'].ActionResources.Resources['g-action'][1].Amount").Number);
        Assert.Equal(9, Run(game, $"return World.creatures['{Host}'].ActionResources.Resources['g-move'][1].Amount").Number);
        Assert.Equal(1, Run(game, "return #Rows('stage_refill')").Number);
        // a second refill in the scope keeps the first amounts as the ones to go back to
        Run(game, $"World.creatures['{Host}'].ActionResources.Resources['g-action'][1].Amount = 0 Settle(GK.Stage.Refill{{ who = '{Host}', action = true }})");
        // the undo is data in the row: run as GK.Fixture runs a row's op, with no closure of this state
        Run(game, "for _, row in ipairs(GK.Stage.Open()) do if row.payload.op then GK.Fixture.RunOp(row.payload.op) end end");
        Assert.Equal(0, Run(game, $"return World.creatures['{Host}'].ActionResources.Resources['g-action'][1].Amount").Number);
        Assert.Equal(1.5, Run(game, $"return World.creatures['{Host}'].ActionResources.Resources['g-move'][1].Amount").Number);
    }

    [Fact]
    public void Karmic_dice_and_inspiration_are_undone_by_data_rows_not_closures()
    {
        var game = Game();
        Run(game, Scope + $$"""
            World.switches.WeightedRolls = true
            World.creatures["{{Guard}}"].ActionResources = { Resources = { ["g-insp"] = { { Amount = 1, MaxAmount = 1 } } } }
            """);
        Run(game, "Settle(GK.Rolls.SetKarmic(false))");
        Assert.True(Run(game, "return GK.Rolls.SetInspiration(3).ok").Boolean);
        Assert.False(Run(game, "return GK.Rolls.SetInspiration(5).ok").Boolean);
        Assert.Contains("ceiling", Run(game, "return GK.Rolls.SetInspiration(5).error").String, StringComparison.Ordinal);
        var ops = Run(game, "local out = {} for _, r in ipairs(GK.Stage.Open()) do if r.payload.op then out[#out + 1] = r.payload.op.op end end table.sort(out) return table.concat(out, ',')").String;
        Assert.Equal("inspiration,karmic", ops);
        Run(game, "for _, row in ipairs(GK.Stage.Open()) do if row.payload.op then GK.Fixture.RunOp(row.payload.op) end end");
        Assert.True(Run(game, "return World.switches.WeightedRolls").Boolean);
        Assert.Equal(1, Run(game, $"return World.creatures['{Guard}'].ActionResources.Resources['g-insp'][1].Amount").Number);
    }

    [Fact]
    public void A_pin_left_by_an_earlier_scope_is_not_taken_as_this_scopes_and_its_clean_takes_the_new_one_off()
    {
        // the night's defect: a pin of the same die found as a row of an earlier scope was kept as
        // on, and the clean of the scope that asked for it did not take it off
        var game = Game();
        Run(game, Scope + $"Settle(GK.Rolls.Pin{{ who = '{Guard}', kind = 'SkillCheck', n = 20 }})");
        Run(game, "GK.EventLog.Scope = GK.EventLog.Seq + 1");
        var again = Run(game, $"return Settle(GK.Rolls.Pin{{ who = '{Guard}', kind = 'SkillCheck', n = 20 }})").Table;
        Assert.True(again.Get("inForce").Boolean);
        var rows = Run(game, "return Rows('roll_pin')").Table;
        Assert.True(rows.Get(rows.Length).Table.Get("payload").Table.Get("kept").IsNil(), "the second pin is made anew, not kept from the earlier scope's row");
        Assert.Contains(Run(game, "local n = 0 for _, r in ipairs(GK.Stage.Open('scope')) do if r.payload.op and r.payload.op.op == 'boost' then n = n + 1 end end return n").Number, new[] { 1.0 });
    }

    [Fact]
    public void The_stage_reads_its_equipment_slots_from_the_games_enumeration_and_a_begin_without_them_is_refused()
    {
        // the night's defect: a baseline whose slot names held no weapon slot the game answers to,
        // so it knew no weapon and put none back
        var none = Game();
        Assert.Contains("equipment slots", Error(none, "GK.Stage.Begin()"), StringComparison.Ordinal);
        var lua = Path.Combine(CopySettings.RepositoryRoot()!, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua");
        var game = Game();
        Run(game, "Ext.Enums.ItemSlot = { Helmet = 0, MeleeMainHand = 3, RangedOffHand = 6, Sentinel = 20, [0] = 'Helmet' }");
        game.DoString(File.ReadAllText(Path.Combine(lua, "Server", "Stage.lua")), null, "Server/Stage.lua");
        Assert.Equal("Helmet,Melee Main Weapon,Ranged Offhand Weapon", Run(game, "return table.concat(GK.Stage.EquipmentSlots(), ',')").String);
    }

    [Fact]
    public void The_kinds_are_the_games_enumeration_without_its_markers_and_initiative()
    {
        var kinds = Run(Game(), "return table.concat(GK.Rolls.Kinds(), ',')").String.Split(',');
        Assert.Equal("Initiative", kinds[^1]);
        Assert.DoesNotContain("None", kinds);
        Assert.DoesNotContain("Sentinel", kinds);
        Assert.Contains("DeathSavingThrow", kinds);
        Assert.Equal(9, kinds.Length);
    }

    [Fact]
    public void A_pin_answers_once_the_boost_is_on_the_creature_and_is_a_ledger_row_and_a_roll_pin_row()
    {
        var game = Game();
        Run(game, Scope);
        var answer = Run(game, $"return Settle(GK.Rolls.Pin{{ who = '{Guard}', kind = 'SavingThrow', n = 1 }})").Table;
        Assert.True(answer.Get("inForce").Boolean);
        Assert.Equal(3, answer.Get("ticks").Number);
        Assert.Equal("MinimumRollResult(SavingThrow,1);MaximumRollResult(SavingThrow,1)", answer.Get("boost").String);
        Assert.Equal("GKPin:MaximumRollResult(SavingThrow,1) GKPin:MinimumRollResult(SavingThrow,1)", Run(game, $"return World.boosts('{Guard}')").String);
        var pins = Run(game, "return GK.Rolls.Pins()").Table;
        Assert.Equal(1, pins.Length);
        Assert.Equal("SavingThrow", pins.Get(1).Table.Get("kind").String);
        Assert.True(pins.Get(1).Table.Get("tracked").Boolean);
        Assert.Equal("cheat", Run(game, "return Rows('scope_track')[1].payload.fixture and GK.EventLog.Kinds.roll_pin.class").String);
        Assert.Equal(Guard, Run(game, "return Rows('roll_pin')[1].payload.who").String);
        // Initiative is the game's Initiative boost, its n a bonus
        var init = Run(game, $"return Settle(GK.Rolls.Pin{{ who = '{Guard}', kind = 'Initiative', n = -100 }})").Table;
        Assert.Equal("Initiative(-100)", init.Get("boost").String);
        Assert.Equal(2, Run(game, "return #GK.Rolls.Pins()").Number);
    }

    [Fact]
    public void A_pins_limit_is_counted_in_server_ticks_so_a_stall_of_the_game_is_no_failure()
    {
        var game = Game();
        Run(game, Scope);
        // a boost the game never takes, and the first look only after seven seconds of real time
        Run(game, "Osi.AddBoosts = function() end");
        Run(game, $"Answer, Check, Within = GK.Unobserved(GK.Rolls.Pin{{ who = '{Host}', kind = 'SkillCheck', n = 2 }})");
        Assert.True(Run(game, "return Within").Number >= 30000);
        Run(game, "World.tick()");
        Assert.True(Run(game, "return Check(7000, false)").IsNil());
        // a running game's 90 ticks are the limit
        Run(game, "World.tick(90)");
        var failed = Run(game, "return Check(10000, false)").Table;
        Assert.False(failed.Get("ok").Boolean);
        Assert.Contains("is not on", failed.Get("error").String, StringComparison.Ordinal);
    }

    [Fact]
    public void Once_takes_the_pin_off_after_the_holders_next_finished_roll_of_the_kind_only()
    {
        var game = Game();
        Run(game, Scope);
        Run(game, $"return Settle(GK.Rolls.Pin{{ who = '{Host}', kind = 'SkillCheck', n = 20, once = true }})");
        // another creature's roll, and the holder's roll of another kind, leave it
        Run(game, $"World.finish('{Guard}', 'SkillCheck', 20) World.finish('{Host}', 'SavingThrow', 7) World.tick()");
        Assert.Equal(1, Run(game, "return #GK.Rolls.Pins()").Number);
        Run(game, $"World.finish('{Host}', 'SkillCheck', 20)");
        Assert.Equal(20, Run(game, $"return GK.Rolls.Recent['{Host}'][2].pinned").Number);
        Assert.True(Run(game, $"return GK.Rolls.Recent['{Host}'][1].pinned").IsNil());
        // the finished rolls wait for their RollResult: taken once, oldest first, then gone
        Assert.Equal(7, Run(game, $"return GK.Rolls.Finished('{Host}', true).natural").Number);
        Assert.Equal(20, Run(game, $"return GK.Rolls.Finished('{Host}', true).natural").Number);
        Assert.True(Run(game, $"return GK.Rolls.Finished('{Host}')").IsNil());
        // one no RollResult takes is dropped after five ticks, so a later roll is not joined to it
        Run(game, $"World.finish('{Guard}', 'SavingThrow', 4) World.tick(6)");
        Assert.True(Run(game, $"return GK.Rolls.Finished('{Guard}')").IsNil());
        Assert.Equal("", Run(game, $"return World.boosts('{Host}')").String);
        Assert.Equal(0, Run(game, "return #GK.Rolls.Pins()").Number);
        var unpin = Run(game, "return Rows('roll_unpin')[1].payload").Table;
        Assert.Equal("once", unpin.Get("why").String);
        Assert.Equal(4, unpin.Get("ticks").Number);
    }

    [Fact]
    public void Unpin_and_the_kits_clear_take_pins_off_through_the_ledger_and_a_new_pin_replaces_the_old()
    {
        var game = Game();
        Run(game, Scope);
        Run(game, $"Settle(GK.Rolls.Pin{{ who = '{Host}', kind = 'SkillCheck', n = 20 }})");
        Run(game, $"Settle(GK.Rolls.Pin{{ who = '{Host}', kind = 'SkillCheck', n = 5 }})");
        Run(game, "World.tick(6)");
        Assert.Equal("GKPin:MaximumRollResult(SkillCheck,5) GKPin:MinimumRollResult(SkillCheck,5)", Run(game, $"return World.boosts('{Host}')").String);
        var unpinned = Run(game, $"return Settle(GK.Rolls.Unpin{{ who = '{Host}', kind = 'SkillCheck' }})").Table;
        Assert.Equal(1, unpinned.Get("removed").Number);
        Assert.Equal("", Run(game, $"return World.boosts('{Host}')").String);
        Assert.Equal(0, Run(game, $"return Settle(GK.Rolls.Unpin{{ who = '{Host}' }}).removed").Number);
        // the fixtures' clear (and the stage's Clean) undo the row with the boost undo of its data
        Run(game, $"Settle(GK.Rolls.Pin{{ who = '{Guard}', kind = 'DeathSavingThrow', n = 20 }})");
        Assert.Equal(1, Run(game, "return GK.Fixture.Clear().undone").Number);
        Run(game, "World.tick(6)");
        Assert.Equal("", Run(game, $"return World.boosts('{Guard}')").String);
        Assert.Equal(0, Run(game, "return #GK.Rolls.Pins()").Number);
    }

    [Fact]
    public void Karmic_dice_is_the_games_option_read_back_from_the_roll_manager_and_set_back_on_clean()
    {
        var game = Game();
        Assert.Contains("GK.Stage.Begin() first", Error(game, "GK.Rolls.SetKarmic(true)"), StringComparison.Ordinal);
        Run(game, Scope);
        var on = Run(game, "return Settle(GK.Rolls.SetKarmic(true))").Table;
        Assert.True(on.Get("ok").Boolean);
        Assert.Equal("WeightedRandom", on.Get("mode").String);
        Assert.False(on.Get("was").Boolean);
        // its undo is a row of the ledger
        Run(game, "for i = #GK.Stage.Tracks, 1, -1 do GK.Stage.UndoTrack(GK.Stage.Tracks[i]) end");
        Assert.False(Run(game, "return World.switches.WeightedRolls").Boolean);
        Assert.Equal("Random", Run(game, "return GK.Rolls.Karmic().mode").String);
    }

    [Fact]
    public void A_deletion_of_what_was_created_in_this_tick_waits_for_the_next()
    {
        var game = Game();
        Run(game, $"GK.Stage.NoteBorn('{Guard}') Deleted = {{}}");
        Assert.True(Run(game, $"return GK.Stage.Unborn('{Guard}', function(id) Deleted[#Deleted + 1] = id end)").Boolean);
        Assert.Equal(0, Run(game, "return #Deleted").Number);
        Run(game, "World.tick(2)");
        Assert.Equal(Guard, Run(game, "return Deleted[1]").String);
        // a tick later it is not put off
        Assert.False(Run(game, $"return GK.Stage.Unborn('{Guard}', function() end)").Boolean);
        Assert.False(Run(game, $"return GK.Stage.Unborn('{Host}', function() end)").Boolean);
    }

    [Fact]
    public void Speed_takes_any_positive_scale_warns_from_16_and_names_the_working_speed()
    {
        var game = Game();
        Assert.Equal(8, Run(game, "return GK.Speed.Set('working').scale").Number);
        var fast = Run(game, "return GK.Speed.Set(20)").Table;
        Assert.Equal(20, fast.Get("scale").Number);
        Assert.Contains("known to break", fast.Get("warning").String, StringComparison.Ordinal);
        Assert.True(Run(game, "return GK.Speed.Set(12).warning").IsNil());
        Assert.Contains("a positive number", Error(game, "GK.Speed.Set(0)"), StringComparison.Ordinal);
        Assert.Contains("a positive number", Error(game, "GK.Speed.Set('fast')"), StringComparison.Ordinal);
        Run(game, "World.tick(3)");
        var clock = Run(game, "return GK.Speed.Clock()").Table;
        Assert.Equal(3, clock.Get("ticks").Number);
    }
}
