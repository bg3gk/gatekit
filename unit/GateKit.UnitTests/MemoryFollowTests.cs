using GateKit.Copies;
using MoonSharp.Interpreter;

namespace GateKit.UnitTests;

/// <summary>
/// GK.Memory's watches follow their component (Shared/Memory.lua): a watch armed on a field, or on
/// an address given its owner, is looked at on the tick; when the component's storage moves, the
/// hits of that tick are held as not the field's and the watch is armed again where the field is
/// now, or closed when the component is gone - a memory_watch row either way. A raw address with no
/// owner stays raw and says so. Against a stand-in of the fork's Ext.Debug: components at
/// addresses that a test moves, four watch slots, and writes that hit whatever watch covers them.
/// </summary>
public sealed class MemoryFollowTests
{
    const string Debug = """
        Mem = { components = {}, watches = {}, hits = {}, frame = 1 }
        local function key(e) return type(e) == "table" and e.Uuid.EntityUuid or e end
        function Mem.place(id, component, address, size, fields)
            Mem.components[id .. "|" .. component] = { address = address, size = size, fields = fields }
        end
        function Mem.move(id, component, address) Mem.components[id .. "|" .. component].address = address end
        function Mem.remove(id, component) Mem.components[id .. "|" .. component] = nil end
        -- a write of the game: a hit on every watch that covers the address
        function Mem.write(address)
            for slot, w in pairs(Mem.watches) do
                if address >= w.Address and address < w.Address + w.Size then
                    Mem.hits[#Mem.hits + 1] = { Slot = slot, Frame = Mem.frame, Address = address }
                end
            end
        end
        World.entities = {}
        Ext.Entity.Get = function(id) World.entities[id] = World.entities[id] or { Uuid = { EntityUuid = id } } return World.entities[id] end
        Ext.Debug = {
            GetFieldAddress = function(e, component, field)
                local c = Mem.components[key(e) .. "|" .. component]
                local f = c and c.fields[field]
                if not f then return nil end
                return { Address = c.address + f, Offset = f, ComponentAddress = c.address, Type = "int32", Size = 4, Scalar = true }
            end,
            GetComponentAddress = function(e, component)
                local c = Mem.components[key(e) .. "|" .. component]
                return c and { Address = c.address, InlineSize = c.size, TotalSize = c.size } or nil
            end,
            WatchWrite = function(address, size, readWrite)
                for slot = 1, 4 do
                    if not Mem.watches[slot] then Mem.watches[slot] = { Address = address, Size = size, ReadWrite = readWrite } return slot end
                end
            end,
            ClearWatch = function(slot)
                if slot then Mem.watches[slot] = nil else Mem.watches = {} end
                return true
            end,
            ListWatches = function()
                local out = {}
                for slot = 1, 4 do
                    local w = Mem.watches[slot]
                    if w then out[#out + 1] = { Slot = slot, Owner = "Server", Address = w.Address, Size = w.Size, ReadWrite = w.ReadWrite } end
                end
                return out
            end,
            ReadWatchHits = function() local h = Mem.hits Mem.hits = {} return h end,
            WatchPoll = function() return #Mem.hits end,
            ReadValue = function() return nil end,
            ClearFreezes = function() return true end,
            ListFreezes = function() return {} end,
        }
        Host = "11111111-1111-1111-1111-111111111111"
        Mem.place(Host, "eoc::HealthComponent", 0x1000, 0x40, { Hp = 0x8 })
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
        var lua = Path.Combine(CopySettings.RepositoryRoot()!, "mods", "GateKit", "Mods", "Gwynerva_GateKit", "ScriptExtender", "Lua");
        var game = BridgeLuaTests.Game("server");
        game.DoString(Debug);
        foreach (var file in new[] { "Shared/Attention.lua", "Shared/Memory.lua" })
            game.DoString(File.ReadAllText(Path.Combine(lua, file)), null, file);
        return game;
    }

    static DynValue Run(Script game, string code) => game.DoString(code);

    [Fact]
    public void A_fields_watch_follows_its_component_and_no_hit_after_the_move_is_the_fields()
    {
        var game = Game();
        Run(game, """
            Field = GK.Memory.Field(Host, "eoc::HealthComponent", "Hp")
            Slot = GK.Memory.Watch(Field)
            Mem.write(0x1008)
            First = GK.Memory.Hits()
            """);
        Assert.Equal("Hp", Run(game, "return First[1].Of.field").String);
        Assert.False(Run(game, "return GK.Attention.Pollers.memory == nil").Boolean);

        // the storage moves; before the next tick the game writes what is now another's memory
        Run(game, """
            Mem.move(Host, "eoc::HealthComponent", 0x2000)
            Mem.write(0x1008)
            World.tick()
            Mem.write(0x1008)   -- the old place: no longer watched
            Mem.write(0x2008)   -- the field where it is now
            Later = GK.Memory.Hits()
            Move = Rows("memory_watch")[1].payload
            """);
        Assert.Equal("rearmed", Run(game, "return Move.did").String);
        Assert.Equal(0x1008, Run(game, "return Move.was").Number);
        Assert.Equal(0x2008, Run(game, "return Move.now").Number);
        Assert.Equal(2, Run(game, "return #Later").Number);
        Assert.True(Run(game, "return Later[1].Moved == true and Later[1].Of == nil").Boolean);   // made in the tick it moved: not the field's
        Assert.Equal("Hp", Run(game, "return Later[2].Of.field").String);
        Assert.Equal(0x2008, Run(game, "return Mem.watches[Move.armed].Address").Number);
        Assert.Equal("rearmed", Run(game, "return GK.Memory.Moved()[Slot].did").String);
    }

    [Fact]
    public void A_hit_stamped_before_the_flush_that_moved_the_component_is_still_the_fields()
    {
        var game = Game();
        // the tracer's counters: a hit is stamped with the flush it was made in
        Run(game, """
            Mem.flush = 5
            Ext.Entity.GetTrace = function() return { Frame = Mem.flush, Flush = Mem.flush, Entities = {} } end
            local write = Mem.write
            Mem.write = function(address) write(address) local h = Mem.hits[#Mem.hits] if h then h.Flush = Mem.flush end end
            Slot = GK.Memory.Watch(GK.Memory.Field(Host, "eoc::HealthComponent", "Hp"))
            World.tick()
            Mem.write(0x1008)                                   -- the field, at flush 5
            Mem.flush = 6
            Mem.move(Host, "eoc::HealthComponent", 0x2000)      -- the flush that moves it
            Mem.write(0x1008)                                   -- another's memory now
            World.tick()
            Hits = GK.Memory.Hits()
            """);
        Assert.Equal(2, Run(game, "return #Hits").Number);
        Assert.Equal("Hp", Run(game, "return Hits[1].Of.field").String);
        Assert.True(Run(game, "return Hits[1].Moved == nil").Boolean);
        Assert.True(Run(game, "return Hits[2].Moved == true and Hits[2].Of == nil").Boolean);
        Assert.Equal(1, Run(game, "return Rows('memory_watch')[1].payload.heldHits").Number);
    }

    [Fact]
    public void An_address_given_its_owner_follows_too_and_is_closed_with_a_row_when_the_component_goes()
    {
        var game = Game();
        Run(game, """
            Slot = GK.Memory.Watch(0x100C, 4, false, { entity = Host, component = "eoc::HealthComponent" })
            Listed = GK.Memory.Watches()[1]
            """);
        Assert.Equal(0xC, Run(game, "return Listed.Follows.offset").Number);
        Assert.True(Run(game, "return Listed.Raw == nil").Boolean);
        Run(game, """
            Mem.move(Host, "eoc::HealthComponent", 0x3000)
            World.tick()
            Moved = Rows("memory_watch")[1].payload
            """);
        Assert.Equal(0x300C, Run(game, "return Moved.now").Number);
        Run(game, """
            Mem.remove(Host, "eoc::HealthComponent")
            Mem.write(0x300C)
            World.tick()
            Closed = Rows("memory_watch")[2].payload
            """);
        Assert.Equal("closed", Run(game, "return Closed.did").String);
        Assert.Equal("its component is gone", Run(game, "return Closed.why").String);
        Assert.True(Run(game, "return next(Mem.watches) == nil").Boolean);
        Assert.True(Run(game, "return GK.Attention.Pollers.memory == nil").Boolean);   // nothing left to look at on the tick
        Assert.True(Run(game, "local h = GK.Memory.Hits() return #h == 1 and h[1].Moved == true").Boolean);
        Assert.Contains("is not inside", Assert.Throws<ScriptRuntimeException>(() =>
            Run(game, "Mem.place(Host, 'eoc::HealthComponent', 0x1000, 0x40, { Hp = 8 }) GK.Memory.Watch(0x1100, 4, false, { entity = Host, component = 'eoc::HealthComponent' })")).DecoratedMessage,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_raw_address_with_no_owner_stays_raw_and_says_so()
    {
        var game = Game();
        Run(game, """
            Slot = GK.Memory.Watch(0x1008, 4)
            Mem.move(Host, "eoc::HealthComponent", 0x2000)
            World.tick()
            Listed = GK.Memory.Watches()[1]
            """);
        Assert.True(Run(game, "return Listed.Raw").Boolean);
        Assert.Equal(0x1008, Run(game, "return Listed.Address").Number);
        Assert.Equal(0, Run(game, "return #Rows('memory_watch')").Number);
        Assert.True(Run(game, "return GK.Attention.Pollers.memory == nil").Boolean);
    }
}
