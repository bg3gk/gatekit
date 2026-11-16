-- GK.Stand.Feed (stand/README.md): what changed since a cursor, by layers, as a view over the event
-- log, GK.See and GK.Heard. The rule held here: every layer is present (scenario empty); a creature
-- that comes into view is in `seen` with `new` once and without it on the next call; a line said
-- through GK.Stand.Say after a cursor is in `said` of the next call and not in a call from a later
-- cursor; the cursor moves forward; the families dice and fight are on after a call; a hit after
-- the cursor is in `happened` with its target's id.
-- Staging (cheat): a neutral human 6 m off, a hostile one beside it, a scripted hit on it
-- (GK.Cheat.Attack). Reading: observation.
-- Run: gatekit --instance <copy> test tests -k stand/feed --speed working

test { level = "GustavX:Basic_Level_A",
       about = "GK.Stand.Feed answers every layer, marks what is new, and gives each row once after its cursor." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local LAYERS = { "situation", "seen", "heard", "waiting", "about_to", "dice", "happened", "said", "scenario" }

local function has(list, pred)
    for _, r in ipairs(list or {}) do if pred(r) then return r end end
end

function run(game)
    game.wait_state("Running", "server", 120)
    local first = step("the feed before", "observation", function() return game.server("return GK.Stand.Feed()", 20) end)
    step("every layer, a cursor, the families on", "observation", function()
        for _, layer in ipairs(LAYERS) do check(type(first[layer]) == "table", "the layer " .. layer, first[layer]) end
        check(#first.situation >= 2, "the place and the party", first.situation)
        check(type(first.cursor) == "string" and first.cursor:match("^%d+:%d+$"), "a cursor seq:heard", first.cursor)
        local on = game.server("return GK.EventLog.SwitchedOn()")
        local joined = "," .. join(on, ",") .. ","
        check(contains(joined, ",dice,") and contains(joined, ",fight,"), "dice and fight on", on)
    end)
    local human = step("a human comes into view", "cheat", function()
        local g = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            return GK.Stage.Spawn{ template = "human", at = { x + 5, y, z + 3 }, faction = "neutral" }[1] ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        return uuid(g)
    end)
    step("the intent said", "cheat", function()
        check(game.server(string.format("return GK.Stand.Say{ say = 'Hello there', at = %q, style = { camera = false } }", human), 20).ok,
            "the line shown")
    end)
    local second = step("the feed after", "observation", function()
        return game.server(string.format("return GK.Stand.Feed(%q)", first.cursor), 20)
    end)
    step("the human new in seen, the line in said", "observation", function()
        local seen = has(second.seen, function(r) return r.target == human end)
        check(seen and seen.new, "the human listed as new", second.seen)
        local said = has(second.said, function(r) return r.target == human end)
        check(said and contains(said.text, "Hello there"), "the line in said", second.said)
        check(second.cursor ~= first.cursor, "the cursor moved", first.cursor, second.cursor)
    end)
    local enemy = step("a hostile human, hit by the host", "cheat", function()
        local g = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            return GK.Stage.Spawn{ template = "human", at = { x + 1.5, y, z }, faction = "hostile" }[1] ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        local hit = uuid(g)
        game.server(string.format("return GK.Cheat.Attack(Osi.GetHostCharacter(), %q)", hit), 30)
        return hit
    end)
    local third = step("the feed after the hit", "observation", function()
        game.wait("server", string.format(
            "(function() for _, r in ipairs(GK.EventLog.Since(0, { kinds = { game_hit = true } }).rows) do if r.payload and tostring(r.payload.who):find(%q, 1, true) then return true end end end)()",
            enemy), 20)
        return game.server(string.format("return GK.Stand.Feed(%q)", second.cursor), 20)
    end)
    step("the hit in happened; nothing said again; the human no longer new", "observation", function()
        check(has(third.happened, function(r) return r.kind == "game_hit" end), "a hit row", third.happened)
        check(not has(third.said, function(r) return r.target == human end), "the earlier line not repeated", third.said)
        local seen = has(third.seen, function(r) return r.target == human end)
        check(seen and not seen.new, "the human listed, not new", third.seen)
    end)
end
