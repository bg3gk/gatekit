using MoonSharp.Interpreter;

namespace GateKit.UnitTests;

/// <summary>
/// GK.Trace (Shared/Trace.lua) against a stand-in of the fork's tracer: the record cut into steps,
/// the flag masks named, the rows of the trace family, and a step's rows bounded.
/// </summary>
public sealed class TraceLuaTests
{
    const string Tracer = """
        Tracer = { on = false, setup = nil, entities = {}, systems = {} }
        Ext.Entity.SetupTracing = function(o) Tracer.setup = o end
        Ext.Entity.EnableTracing = function(on) Tracer.on = on end
        Ext.Entity.ClearTrace = function() Tracer.entities, Tracer.systems = {}, {} end
        Ext.Entity.GetTrace = function()
            return { Entities = Tracer.entities, SystemChanges = Tracer.systems, Frame = 10, Flush = 11, DroppedSystemChanges = 0 }
        end
        -- one change of a component of an entity, as the fork logs it
        function Tracer.change(handle, uuid, name, mask, frame)
            local e = Tracer.entities[handle] or { Entity = World.entity(handle, { Uuid = { EntityUuid = uuid } }), FlagMask = 0, Components = {} }
            Tracer.entities[handle] = e
            e.Components[#e.Components + 1] = { Name = name, FlagMask = mask, Count = 1, Frame = frame, Flush = frame + 1,
                LastFrame = frame, LastFlush = frame + 1, DroppedEvents = 0, Events = { { FlagMask = mask, Frame = frame, Flush = frame + 1, Order = 1 } } }
        end
        function Rows(kind)
            local out = {}
            for seq = GK.EventLog.First, GK.EventLog.Seq do
                local r = GK.EventLog.Rows[seq]
                if r and r.kind == kind then out[#out + 1] = r end
            end
            return out
        end
        """;

    static Script Game()
    {
        var game = BridgeLuaTests.Game("server", "Shared/Trace.lua");
        game.DoString(Tracer);
        return game;
    }

    [Fact]
    public void A_trace_cut_into_steps_gives_each_step_its_own_changes_with_the_flags_named()
    {
        var game = Game();
        game.DoString("""
            assert(GK.Trace.Begin{ entities = {}, components = { "Health" }, step = "before" }.ok)
            Tracer.change("e1", "u1", "eoc::HealthComponent", 4 + 16, 5)
            First = GK.Trace.Step("the hit")
            Tracer.change("e1", "u1", "eoc::HealthComponent", 32, 7)
            Tracer.change("e2", "u2", "esv::SomeOneFrameComponent", 1 + 8, 7)
            Last = GK.Trace.End()
            """);
        Assert.Equal("before", game.DoString("return First.step").String);
        Assert.Equal(1, game.DoString("return First.changes").Number);
        Assert.Equal("the hit", game.DoString("return Last.step").String);
        Assert.Equal(2, game.DoString("return Last.changes").Number);
        Assert.False(game.DoString("return Tracer.on").Boolean);
        // the masks as the fork defines them
        Assert.Equal("Replicate|ReplicatedComponent", game.DoString("return Rows('trace_change')[1].payload.flags").String);
        Assert.Equal("u1", game.DoString("return Rows('trace_change')[1].payload.uuid").String);
        var kinds = game.DoString("local k = {} for _, r in ipairs(Rows('trace_change')) do k[#k + 1] = r.payload.step .. ':' .. r.payload.flags end table.sort(k) return table.concat(k, ',')").String;
        Assert.Equal("before:Replicate|ReplicatedComponent,the hit:Create|OneFrame,the hit:Modify", kinds);
        Assert.Equal(2, game.DoString("return #Rows('trace_step')").Number);
        // the family goes off with the trace: no row is written after the end
        Assert.Null(game.DoString("return GK.EventLog.Append('trace_end', {})").ToObject());
    }

    [Fact]
    public void Status_says_the_step_under_way_and_after_the_end_none_only_the_last_that_ended()
    {
        var game = Game();
        game.DoString("""
            GK.Trace.Begin{ step = "before" }
            During = GK.Trace.Status()
            GK.Trace.Step("the hit")
            Hit = GK.Trace.Status()
            GK.Trace.End()
            After = GK.Trace.Status()
            """);
        Assert.Equal("before", game.DoString("return During.step").String);
        Assert.True(game.DoString("return During.last == nil").Boolean);
        Assert.Equal("the hit", game.DoString("return Hit.step").String);
        Assert.Equal("before", game.DoString("return Hit.last").String);
        Assert.False(game.DoString("return After.on").Boolean);
        Assert.True(game.DoString("return After.step == nil and After.options == nil").Boolean);
        Assert.Equal("the hit", game.DoString("return After.last").String);
    }

    [Fact]
    public void A_step_writes_at_most_its_bound_of_rows_and_counts_the_rest()
    {
        var game = Game();
        game.DoString("""
            GK.Trace.Begin{ step = "many" }
            for i = 1, 600 do Tracer.change("e" .. i, "u" .. i, "eoc::HealthComponent", 4, 3) end
            Many = GK.Trace.End()
            """);
        Assert.Equal(600, game.DoString("return Many.changes").Number);
        Assert.Equal(500, game.DoString("return Many.written").Number);
        Assert.Equal(500, game.DoString("return #Rows('trace_change')").Number);
    }

    [Fact]
    public void A_setup_the_tracer_refuses_is_answered_and_nothing_is_switched_on()
    {
        var game = Game();
        game.DoString("Ext.Entity.SetupTracing = function() error('unknown component Nope') end");
        var answer = game.DoString("return GK.Trace.Begin{ components = { 'Nope' } }").Table;
        Assert.False(answer.Get("ok").Boolean);
        Assert.Contains("unknown component Nope", answer.Get("error").String, StringComparison.Ordinal);
        Assert.False(game.DoString("return GK.Trace.Status().on").Boolean);
        Assert.False(game.DoString("return Tracer.on").Boolean);
    }
}
