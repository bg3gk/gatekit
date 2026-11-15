using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.UnitTests;

/// <summary>
/// The game side of the bridge (Shared/Bridge.lua) run in a Lua interpreter with its real event log
/// against a stand-in of Script Extender: a file system in memory, the tick handlers called by hand,
/// and a monotonic clock that can jump while no tick runs - a game that stands still.
/// </summary>
public sealed class BridgeLuaTests
{
    internal const string World = """
        World = { ms = 0, tickers = {}, files = {}, deferred = {} }
        function World.tick(n, ms)
            for _ = 1, n or 1 do
                World.ms = World.ms + (ms or 33)
                for _, f in ipairs(World.tickers) do f({}) end
            end
        end
        -- the clock goes on and no tick runs
        function World.stall(ms) World.ms = World.ms + ms end
        function World.send(side, seq, id, code)
            local path = "Gwynerva_GateKit/queue_" .. side .. ".jsonl"
            World.files[path] = (World.files[path] or "") .. json.serialize({ seq = seq, id = id, code = code }) .. "\n"
        end
        function World.answer(side, id)
            local text = World.files["Gwynerva_GateKit/res_" .. side .. "_" .. id .. ".json"]
            return text and json.parse(text) or nil
        end
        function World.state(side)
            local text = World.files["Gwynerva_GateKit/state_" .. side .. ".json"]
            return text and json.parse(text) or nil
        end
        Ext = {
            IsServer = function() return SIDE == "server" end,
            Utils = { GetGameState = function() return "Running" end, MonotonicTime = function() return World.ms end,
                      GetGlobalSwitches = function() return {} end, PrintError = function() end, PrintWarning = function() end },
            Timer = { MicrosecTime = function() return World.ms * 1000 end },
            IO = { LoadFile = function(path) if path:find("EventKinds", 1, true) then return REGISTRY end return World.files[path] end,
                   SaveFile = function(path, text) World.files[path] = text end },
            Json = { Parse = function(text) return json.parse(text) end, Stringify = function(value) return json.serialize(value) end },
            Events = { GameStateChanged = { Subscribe = function() end },
                       Tick = { Subscribe = function(_, f) World.tickers[#World.tickers + 1] = f end } },
            Entity = { OnCreate = function(name, f) World.on("create", name, f) end, OnDestroy = function(name, f) World.on("destroy", name, f) end,
                       OnChange = function(name, f) World.on("change", name, f) end,
                       GetAllEntitiesWithComponent = function(name) return World.with[name] or {} end, Get = function() return nil end },
        }
        World.hooks, World.with = {}, {}
        function World.on(what, name, f) local key = what .. ":" .. name World.hooks[key] = World.hooks[key] or {} table.insert(World.hooks[key], f) end
        -- the engine's event for a component made or removed
        function World.raise(what, name, entity) for _, f in ipairs(World.hooks[what .. ":" .. name] or {}) do f(entity) end end
        -- an entity: a table whose components are fields, its handle a name
        function World.entity(handle, components)
            return setmetatable(components or {}, { __tostring = function() return handle end })
        end
        """;

    internal static Script Game(string side = "server", params string[] more)
    {
        var lua = Path.Combine(CopySettings.RepositoryRoot()!, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua");
        var script = new Script(CoreModules.Preset_Complete);
        script.Globals["REGISTRY"] = File.ReadAllText(Path.Combine(lua, "Shared", "EventKinds.json"));
        script.Globals["SIDE"] = side;
        script.DoString(World, null, "world");
        foreach (var file in new[] { "Shared/Bridge.lua", "Shared/EventLog.lua" }.Concat(more))
            script.DoString(File.ReadAllText(Path.Combine(lua, file)), null, file);
        script.DoString($"GK.Start('{side}')");
        return script;
    }

    static DynValue Run(Script game, string code) => game.DoString(code);

    const string NeverSeen = "return GK.Observe({ ok = true, did = 'asked' }, function(ticks, final) if final then return { ok = false, at = ticks } end end, 1000)";

    [Fact]
    public void A_game_that_stands_still_eats_nothing_of_an_observed_answers_limit()
    {
        var game = Game();
        Run(game, $"World.send('server', 1, 'a', [[{NeverSeen}]]) World.tick(12)");
        Assert.True(Run(game, "return World.answer('server', 'a') == nil").Boolean);
        // seven seconds of the clock with one tick: the limit of 1000 ms is 30 ticks, a few have run
        Run(game, "World.stall(7000) World.tick(1)");
        Assert.True(Run(game, "return World.answer('server', 'a') == nil").Boolean);
        Run(game, "World.tick(40)");
        var answer = Run(game, "return World.answer('server', 'a').result").Table;
        Assert.False(answer.Get("ok").Boolean);
        Assert.Equal(30, answer.Get("at").Number);
        Assert.Equal(30, answer.Get("waitedTicks").Number);
    }

    [Fact]
    public void A_server_ends_the_limit_in_its_own_ticks_whatever_the_clock_says()
    {
        // a server tick that takes 5 ms of the clock is still a tick: 30 of them end a limit of 1000 ms
        var game = Game();
        Run(game, $"World.send('server', 1, 'b', [[{NeverSeen}]]) World.tick(20, 5)");
        Assert.True(Run(game, "return World.answer('server', 'b') == nil").Boolean);
        Run(game, "World.tick(20, 5)");
        Assert.Equal(30, Run(game, "return World.answer('server', 'b').result.at").Number);
    }

    [Fact]
    public void A_client_drawing_fast_counts_its_frames_by_their_time_and_a_hanging_frame_eats_little()
    {
        // the night's defect: a client drawing 144 frames a second counted each frame as a tick, and
        // a limit of 30 s of a running game ran out in 5 s
        var game = Game("client");
        Run(game, $"World.send('client', 1, 'b', [[{NeverSeen}]]) World.tick(100, 5)");
        // 100 frames of 5 ms: half a second of a running game, half the limit
        Assert.True(Run(game, "return World.answer('client', 'b') == nil").Boolean);
        // a frame that hangs for 7 s counts as 100 ms
        Run(game, "World.stall(7000) World.tick(1, 0)");
        Assert.True(Run(game, "return World.answer('client', 'b') == nil").Boolean);
        Run(game, "World.tick(90, 5)");
        Assert.True(Run(game, "return World.answer('client', 'b').result.at").Number >= 30);
    }

    [Fact]
    public void A_check_with_nothing_to_say_at_its_limit_is_answered_that_the_event_never_came()
    {
        var game = Game();
        Run(game, "World.send('server', 1, 'c', [[return GK.Observe({ ok = true, did = 'asked' }, function() end, 100)]]) World.tick(20)");
        var answer = Run(game, "return World.answer('server', 'c').result").Table;
        Assert.False(answer.Get("ok").Boolean);
        Assert.Contains("the event never came", answer.Get("error").String, StringComparison.Ordinal);
        Assert.Equal("asked", answer.Get("attempted").Table.Get("did").String);
    }

    [Fact]
    public void A_nested_wait_follows_the_inner_answer_with_the_bridges_own_loop()
    {
        var game = Game();
        Run(game, """
            function Inner() return GK.Observe({ ok = true }, function(ticks) if ticks >= 4 then return { ok = true, inner = ticks } end end, 1000) end
            function Outer()
                local step = GK.Follow(Inner())
                return GK.Observe({ ok = true }, function() local v = step() if v then return { ok = true, got = v.inner } end end, 1000)
            end
            """);
        Run(game, "World.send('server', 1, 'd', [[return Outer()]]) World.tick(20)");
        Assert.Equal(4, Run(game, "return World.answer('server', 'd').result.got").Number);
    }

    [Fact]
    public void The_controlled_character_is_kept_from_the_games_own_events_and_never_listed_from_the_entity_system()
    {
        var game = Game("client", "Client/Api.lua");
        Run(game, """
            Hero = World.entity("hero", { ClientControl = {}, Uuid = { EntityUuid = "h" } })
            World.raise("create", "ClientControl", Hero)
            -- a list asked of the entity system now (from the render thread) would come back empty
            World.with.ClientControl = {}
            """);
        Assert.Equal("h", Run(game, "return GK.Controlled()[1].Uuid.EntityUuid").String);
        Run(game, "World.raise('destroy', 'ClientControl', Hero)");
        Assert.Equal(0, Run(game, "return #GK.Controlled()").Number);
    }

    [Fact]
    public void A_bridge_loaded_inside_a_level_knows_who_is_controlled_already()
    {
        var game = Game("client");
        Run(game, """World.with.ClientControl = { World.entity("hero", { ClientControl = {}, Uuid = { EntityUuid = "h" } }) }""");
        var lua = Path.Combine(CopySettings.RepositoryRoot()!, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua", "Client", "Api.lua");
        game.DoString(File.ReadAllText(lua), null, "Client/Api.lua");
        Assert.Equal(1, Run(game, "return #GK.Controlled()").Number);
    }

    [Theory]
    [InlineData("p1", "p1", true)]
    [InlineData("p1", "p2", false)]
    [InlineData("p1", null, false)]
    [InlineData(null, null, true)]
    public void A_previous_boot_is_carried_only_from_the_same_game_process(string? before, string? now, bool carried)
    {
        var lua = Path.Combine(CopySettings.RepositoryRoot()!, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua");
        var script = new Script(CoreModules.Preset_Complete);
        script.Globals["REGISTRY"] = File.ReadAllText(Path.Combine(lua, "Shared", "EventKinds.json"));
        script.Globals["SIDE"] = "server";
        script.DoString(World);
        script.DoString($$"""
            World.files["Gwynerva_GateKit/events_server.json"] = json.serialize({ side = "server", boot = 4, segment = 1, first = 1, seq = 1, rows = 128 {{(before is null ? "" : $", process = \"{before}\"")}} })
            World.files["Gwynerva_GateKit/events_server_4_1.jsonl"] = '{"seq":1,"tick":1,"t":1,"kind":"note","knows":"omniscient"}\n'
            {{(now is null ? "" : $"World.files[\"Gwynerva_GateKit/process.txt\"] = \"{now}\"")}}
            """);
        foreach (var file in new[] { "Shared/Bridge.lua", "Shared/EventLog.lua" })
            script.DoString(File.ReadAllText(Path.Combine(lua, file)), null, file);
        Assert.Equal(carried, script.DoString("return GK.EventLog.Previous() ~= nil").Boolean);
    }

    [Fact]
    public void A_one_click_attack_is_not_taken_for_the_first_click_of_two()
    {
        // the night's defect: the click of an attack on a creature adds NeedsPreviewToActivate to the
        // task as Throw's first click does, and Confirm answered it as a pick waiting for a second click
        var game = Game("client", "Client/Api.lua");
        Run(game, "GK.Gui = {} GK.Cheat = {} Ext.Net = { CreateChannel = function() return nil end }");
        var aim = Path.Combine(CopySettings.RepositoryRoot()!, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua", "Client", "Aim.lua");
        game.DoString(File.ReadAllText(aim), null, "Client/Aim.lua");
        Run(game, """
            Goblin = World.entity("goblin", { Uuid = { EntityUuid = "g" } })
            Dagger = World.entity("dagger", { Uuid = { EntityUuid = "d" } })
            Attack = { Flags = "NeedsPreviewToActivate", Result = { Target = { Target = Goblin } }, Target = { Target = Goblin } }
            Throw = { Flags = "NeedsPreviewToActivate", Result = { Target = { Target = Dagger } }, Target = { } }
            Plain = { Flags = "", Result = { Target = { Target = Goblin } }, Target = { } }
            """);
        Assert.True(Run(game, "return GK.Gui.FirstClickPicked(Attack) == nil").Boolean);
        Assert.Equal("d", Run(game, "return GK.Gui.FirstClickPicked(Throw)").String);
        Assert.True(Run(game, "return GK.Gui.FirstClickPicked(Plain) == nil").Boolean);
    }

    [Fact]
    public void The_heartbeat_carries_the_sides_tick_count()
    {
        var game = Game();
        Run(game, "World.tick(40)");
        Assert.True(Run(game, "return World.state('server').tick").Number >= 30);
    }
}
