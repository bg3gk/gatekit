-- What GateKit's Script Extender patches add (docs/se-fork.md). Each part runs only when the
-- running Script Extender has the function, so on the stock build this scenario passes and says
-- what it skipped. To run it against the local build:
--
--     set GATEKIT_SE=<se-fork>\builds\v34
--     gatekit test -k se_fork
-- gatekit:allow-non-english  the game's own strings and other alphabets, to prove the channel
-- carries them unchanged: a clipboard and an injected keystroke that only work in ASCII are
-- broken for most of the people who play this game.

scenario { level = "GustavX:TUT_Avernus_C", mods = {} }

local MARKER = "gatekit-se-fork-marker"

-- Collects ConsoleMessage events on one side; the handler prints, which must not feed itself.
local COLLECT = ([[
local kit = Mods.GateKit
kit.SeFork = { seen = {}, calls = 0 }
kit.SeFork.handler = Ext.Events.ConsoleMessage:Subscribe(function(e)
    kit.SeFork.calls = kit.SeFork.calls + 1
    print("[se_fork] saw console message " .. tostring(e.Index))
    -- What this handler prints must never come back to it as a message.
    if string.find(e.Message, "[se_fork] saw console message", 1, true) then kit.SeFork.echoed = true end
    if string.find(e.Message, "%s", 1, true) then
        table.insert(kit.SeFork.seen, { index = e.Index, type = tostring(e.Type), message = e.Message })
    end
end)
Ext.Timer.WaitFor(50, function() error("%s") end)
return true
]]):format(MARKER, MARKER)

local WINDOW = [[
local kit = Mods.GateKit
local w = Ext.IMGUI.NewWindow("SE fork scenario")
w:SetSize({ 300, 200 })
local child = w:AddChildWindow("lines")
for i = 1, 200 do child:AddText("line " .. i) end
kit.SeForkWindow = { window = w, child = child }
return true
]]

local FIELD = [[
local kit = Mods.GateKit
local w = Ext.IMGUI.NewWindow("SE fork field")
w:SetSize({ 400, 120 })
local input = w:AddInputText("##line", "")
local state = { window = w, input = input }
input.OnHistory = function(_, direction)
    state.history = direction
    if direction == -1 then
        input.Text = "recalled"
        input.CursorPos = 3
    end
end
input.OnCompletion = function(_, cursor) state.completion = cursor end
kit.SeForkField = state
return true
]]

function run(game)
    local has = game.client([[return {
        clipboard = Ext.IMGUI.GetClipboardText ~= nil,
        -- Ext.Events.<any name> is never nil: an unknown name gives a stub without NextIndex
        console = Ext.Events.ConsoleMessage.NextIndex ~= nil,
        version = Ext.Utils.Version() }]])
    local skipped = {}
    for _, name in ipairs({ "clipboard", "console" }) do
        if not has[name] then skipped[#skipped + 1] = name end
    end

    if has.clipboard then
        local text = "GateKit clipboard ✓ проверка"
        local result = game.client(([[
            local before = Ext.IMGUI.GetClipboardText()
            local stored = Ext.IMGUI.SetClipboardText("%s")
            local after = Ext.IMGUI.GetClipboardText()
            if before then Ext.IMGUI.SetClipboardText(before) end
            return { stored = stored, after = after, restored = Ext.IMGUI.GetClipboardText() == before or before == nil }
        ]]):format(text))
        check(result.stored == true, "nothing was stored", result)
        check(result.after == text, "the clipboard holds something else", result)
        check(result.restored, "the user's clipboard was not put back", result)
    end

    -- Window state: properties exist on the patched build only.
    game.client(WINDOW)
    local ok, problem = pcall(function()
        local patched = game.wait("client", [[(function()
            local c = Mods.GateKit.SeForkWindow.child
            local ok, max = pcall(function() return c.LastScrollMax end)
            if not ok then return "absent" end
            return max[2] > 0 and "ready" or nil
        end)()]], 10)
        if patched == "absent" then
            skipped[#skipped + 1] = "window state"
        else
            game.client("Mods.GateKit.SeForkWindow.child:SetScroll({ 0, 120 })")
            local scroll = game.wait("client", [[(function()
                local c = Mods.GateKit.SeForkWindow.child
                return c.LastScroll[2] == 120 and { y = c.LastScroll[2], max = c.LastScrollMax[2],
                    focused = c.Focused, hovered = c.Hovered } or nil
            end)()]], 10)
            check(scroll.max > 120, "the scrollable area is too small", scroll)
            check(type(scroll.focused) == "boolean" and type(scroll.hovered) == "boolean", "focus and hover", scroll)
        end
    end)
    game.client([[local s = Mods.GateKit.SeForkWindow
        if s then s.window:Destroy() Mods.GateKit.SeForkWindow = nil end]])
    check(ok, tostring(problem))

    if has.console then
        for _, side in ipairs({ "client", "server" }) do
            game.lua(side, COLLECT)
            local done, trouble = pcall(function()
                local seen = game.wait(side, "#Mods.GateKit.SeFork.seen > 0 and Mods.GateKit.SeFork.seen", 15)
                -- The error raised inside the timer callback is reported by C++, not by print.
                local found = false
                for _, message in ipairs(seen) do
                    if message.type == "Error" and contains(message.message, MARKER) then found = true end
                end
                check(found, "no C++ error message on " .. side, seen)
                -- The handler prints one line per message; none of those lines may be handed back
                -- to it (other mods keep printing meanwhile, so counting calls would prove nothing).
                game.wait(side, ("Ext.Timer.MonotonicTime() > %d")
                    :format(game.lua(side, "return Ext.Timer.MonotonicTime()") + 700))
                local state = game.lua(side, "return { calls = Mods.GateKit.SeFork.calls, echoed = Mods.GateKit.SeFork.echoed }")
                check(state.calls >= 1 and not state.echoed, "the handler heard itself on " .. side, state)
            end)
            game.lua(side, [[local s = Mods.GateKit.SeFork
                if s then Ext.Events.ConsoleMessage:Unsubscribe(s.handler) Mods.GateKit.SeFork = nil end]])
            check(done, tostring(trouble))
        end
    end

    -- A text field driven like a command line, with injected input only (no real keyboard).
    if game.client("return Ext.Input.InjectText ~= nil") then
        game.client(FIELD)
        local done, trouble = pcall(function()
            game.wait("client", "Mods.GateKit.SeForkField.window.LastSize[1] > 0", 10)
            game.client("Mods.GateKit.SeForkField.input:Activate()")
            game.client('Ext.Input.InjectText("hello мир")')
            local typed = game.wait("client", [[(function()
                local i = Mods.GateKit.SeForkField.input
                return i.Text == "hello мир" and { text = i.Text, cursor = i.CursorPos } or nil
            end)()]], 10)
            check(typed.cursor == bytes("hello мир"), "the cursor counts bytes", typed)
            game.client('Ext.Input.InjectKeyPress("UP")')
            game.wait("client", [[(function()
                local s = Mods.GateKit.SeForkField
                return s.history == -1 and s.input.Text == "recalled" and s.input.CursorPos == 3
                    and { cursor = s.input.CursorPos } or nil
            end)()]], 10)
            game.client('Ext.Input.InjectKeyPress("TAB")')
            game.wait("client", "Mods.GateKit.SeForkField.completion == 3", 10)
            game.client("Mods.GateKit.SeForkField.input:SetSelection(2, 6)")
            game.wait("client", [[(function()
                local i = Mods.GateKit.SeForkField.input
                return i.SelectionStart == 2 and i.SelectionEnd == 6
            end)()]], 10)
            game.client('Ext.Input.InjectKeyPress("DOWN")')
            game.wait("client", "Mods.GateKit.SeForkField.history == 1", 10)
        end)
        game.client([[local s = Mods.GateKit.SeForkField
            if s then s.window:Destroy() Mods.GateKit.SeForkField = nil end]])
        check(done, tostring(trouble))
    else
        skipped[#skipped + 1] = "input callbacks and injection"
    end

    if game.server("return Ext.Osiris.GetFunctions ~= nil") then
        local osiris = game.server([[
            local all, byType, sample = Ext.Osiris.GetFunctions(), {}, nil
            for _, f in ipairs(all) do
                byType[f.Type] = (byType[f.Type] or 0) + 1
                if f.Name == "GetHitpoints" then sample = f end
            end
            return { count = #all, byType = byType, sample = sample }]])
        check(osiris.count > 1000, "too few Osiris functions", osiris.count)
        for _, kind in ipairs({ "Event", "Query", "Call", "Database" }) do
            check((osiris.byType[kind] or 0) > 0, "no functions of kind " .. kind, osiris.byType)
        end
        local sample = osiris.sample
        check(sample and sample.Type == "Query" and sample.Arity == 2, "GetHitpoints", sample)
        eq({ sample.Params[1].Out, sample.Params[2].Out }, { false, true }, "the parameter directions")
        check(sample.Params[2].BaseType == "integer", "the parameter type", sample)
    else
        skipped[#skipped + 1] = "osiris names"
    end

    -- Raw memory access for hot checks of structure layouts (developer mode only).
    if game.server("return Ext.Debug.ReadMemory ~= nil") then
        local raw = game.server([[
            local host = Ext.Entity.Get(Osi.GetHostCharacter())
            local where = Ext.Debug.GetComponentAddress(host, "eoc::HealthComponent")
            local bytes = Ext.Debug.ReadMemory(where.Address, where.TotalSize)
            -- The current hit points are somewhere in there as a 32-bit integer.
            local hp, found = host.Health.Hp, nil
            for offset = 0, #bytes - 4, 4 do
                if string.unpack("<i4", bytes, offset + 1) == hp then found = offset break end
            end
            local module = Ext.Debug.GetModuleInfo()
            local header = Ext.Debug.ReadMemory(module.Base, 2)
            local types = Ext.Debug.GetComponentTypes()
            local systems = Ext.Debug.GetSystemTypes()
            -- Writing the same bytes back changes nothing; address 8 is never mapped.
            local same = Ext.Debug.WriteMemory(where.Address, bytes)
            return { size = where.TotalSize, read = #bytes, hp = hp, hpOffset = found, header = header,
                     moduleSize = module.Size, types = #types, systems = #systems, wroteSame = same,
                     hpAfter = host.Health.Hp, badRead = Ext.Debug.ReadMemory(8, 4) == nil,
                     badWrite = Ext.Debug.WriteMemory(8, "abcd"),
                     unknown = Ext.Debug.GetComponentAddress(host, "no::SuchComponent") == nil }]])
        check(raw.read == raw.size and raw.size > 0, "the component was not read whole", raw)
        check(raw.hpOffset ~= nil, "the hit points are not in the component's bytes", raw)
        check(raw.header == "MZ" and raw.moduleSize > 50000000, "the module header", raw)
        check(raw.types > 2000 and raw.systems > 500, "the type tables are too small", raw)
        check(raw.wroteSame == true and raw.hpAfter == raw.hp, "writing the same bytes changed something", raw)
        check(raw.badRead == true and raw.badWrite == false and raw.unknown == true, "a bad address was not refused", raw)
    else
        skipped[#skipped + 1] = "raw memory access"
    end

    -- Fields carry the game's own names; the names Script Extender used before still work
    -- and read the same data.
    local names = game.server([[
        local host = Ext.Entity.Get(Osi.GetHostCharacter())
        local out = {}
        local okNew, abilities = pcall(function() return host.BaseStats.Abilities end)
        out.new = okNew and abilities ~= nil
        if out.new then
            local old = host.BaseStats.BaseAbilities
            out.count = #abilities
            out.same = #old == #abilities
            for i = 1, #abilities do out.same = out.same and old[i] == abilities[i] end
            -- The character's bag is an entity of its own.
            local bag = host.InventoryOwner.PrimaryInventory.InventoryContainer
            local slots, items = 0, 0
            for _ in pairs(bag.Slots) do slots = slots + 1 end
            for _ in pairs(bag.Items) do items = items + 1 end
            out.slots, out.items = slots, items
        end
        return out]])
    if names.new then
        check(names.count == 7 and names.same == true, "the abilities differ by name", names)
        check(names.slots == names.items and names.slots > 0, "the bag", names)
    else
        skipped[#skipped + 1] = "the game's field names"
    end

    print(("Script Extender v%s; skipped (not in this build): %s"):format(has.version, join(skipped, ", ", "nothing")))
end
