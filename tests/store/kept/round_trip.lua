-- What a store holds when the game saves is what it holds after that save is loaded, to the last
-- bit of a number, and a store made after the save is not there after the load
-- (docs/contracts/lua-api.md, "GK.Store": "a load returns the store to the save's state"). The
-- value has the shapes a mod keeps: a list, tables nested in tables, numbers with fractions
-- (one third and a tiny negative, which a save that writes numbers as short text would round), a
-- boolean, texts with Cyrillic and Chinese letters. It is compared inside the game, value by
-- value with ==, so nothing is rounded on the way to the host. The save and the load are the
-- copy's checkpoint and its reset (game.checkpoint, game.reset: the game's own autosave and
-- Osi.LoadGame). A load ends the stage scope with the Lua state, so it is opened again over the
-- loaded world. The copy's checkpoint is also what the reset ladder reloads for the tests that
-- follow (its `venue` rung), so the test does not leave its own there: its last step takes the
-- store out and makes the checkpoint again, of the world as the test found it; a run that fails
-- before that step leaves a checkpoint with the store `gk_round_trip` in it.
-- Staging (cheat): the writes, the saves, the load, the scope. Reading: observation.
-- Limit: 60 s - two saves and a load, about a quarter of a minute each at most.
-- Run: gatekit --instance <copy> test tests/store -k store/kept/round_trip

test { level = "GustavX:Basic_Level_A", surprises = { "loading" },
       about = "What a store holds at a save is what it holds after that save is loaded." }

local VALUE = [[{
    -- other alphabets, escaped (Cyrillic and a CJK character), to prove a save keeps them whole
    books = { "\u{422}\u{445}\u{430}\u{440} \u{41A}\u{43E}\u{434}\u{435}\u{43A}\u{441}", "\u{66F8}", "Necromancy of Thay" },
    at = { x = 1 / 3, y = -2.5e-7, z = 1234567.875, far = 1e300, near = 1e-50, big = 9007199254740993 },
    deep = { a = { b = { c = { 0.1, 0.2, 0.30000000000000004 } } } },
    read = false,
    shelf = { { n = 1, kept = true }, { n = 2, kept = false } },
}]]

-- "same" when the store is VALUE exactly; otherwise every difference by path, sorted
local SAME = [[
    local out = {}
    local function show(v) return type(v) == "number" and string.format("%.17g", v) or tostring(v) end
    local function diff(a, b, at)
        if type(a) ~= type(b) then out[#out + 1] = at .. ": " .. show(a) .. " against " .. show(b) return end
        if type(a) ~= "table" then
            if a ~= b then out[#out + 1] = at .. ": " .. show(a) .. " against " .. show(b) end
            return
        end
        for k, v in pairs(a) do diff(v, b[k], at .. "." .. tostring(k)) end
        for k, v in pairs(b) do if a[k] == nil then diff(nil, v, at .. "." .. tostring(k)) end end
    end
    diff(GK.Store.Get("gk_round_trip"), ]] .. VALUE .. [[, "")
    table.sort(out)
    return #out == 0 and "same" or table.concat(out, "; ")
]]

local function names(game)
    local out = {}
    for _, s in ipairs(game.server("return GK.Store.Names()") or {}) do out[#out + 1] = s.name end
    return out
end

function run(game)
    game.wait_state("Running", "server", 120)
    step("the value set", "cheat", function()
        check(game.server("return GK.Store.Set('gk_round_trip', nil, " .. VALUE .. ")").ok, "the store is written")
        eq(game.server(SAME), "same", "read back before the save")
    end)
    step("the game saved", "cheat", function() game.checkpoint() end)
    step("the value changed, a second store made", "cheat", function()
        check(game.server("return GK.Store.Set('gk_round_trip', 'at.x', 0.5)").ok, "a field changed")
        check(game.server("return GK.Store.Set('gk_round_trip', 'books', { '\u{434}\u{440}\u{443}\u{433}\u{430}\u{44F}' })").ok, "the list replaced")
        check(game.server("return GK.Store.Set('gk_round_trip_after', 'made', true)").ok, "a second store")
    end)
    step("the change is in force", "observation", function()
        check(game.server(SAME) ~= "same", "the store differs from the saved value")
        check(has(names(game), "gk_round_trip_after"), "the second store is there", names(game))
    end)
    step("the save loaded", "cheat", function() game.reset() end)
    step("a stage scope over the loaded world", "cheat", function()
        check(game.server("return GK.Stage.Begin{ fresh = true }").level ~= nil, "the scope is open")
    end)
    step("the stores are the save's", "observation", function()
        local now = names(game)
        check(has(now, "gk_round_trip"), "the first store is in the save", now)
        check(not has(now, "gk_round_trip_after"), "the store made after the save is gone", now)
    end)
    step("the value is the save's", "observation", function()
        eq(game.server(SAME), "same", "the value is back exactly")
    end)
    step("the store taken out, and the copy's checkpoint made again without it", "cheat", function()
        check(game.server("return GK.Store.Drop('gk_round_trip')").ok, "the store is dropped")
        check(not has(names(game), "gk_round_trip"), "the save's variables hold no store of this test", names(game))
        game.checkpoint()
    end)
end
