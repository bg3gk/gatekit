-- AI.Now (mods/AI/README.md): the `now` block of every answer of the bridge is a delta. The rule
-- held here: a look answers the whole picture (where, fight, party, waiting); two steps with
-- nothing between answer an empty block, twice; a creature that comes into view is in `near` of
-- the next answer once, marked `+`, with its handle, and not in the answer after; a step's answer
-- has its five keys; the block stays under the hard cap; a row `ai_step` keeps the step with its
-- ticks and the block's length.
-- Staging (cheat): a neutral human 5 m from the host. Every call of the bridge writes its row, so
-- the steps that call it are cheat steps.
-- Run: gatekit --instance <copy> test tests -k ai/now --speed working

test { level = "GustavX:Basic_Level_A",
       about = "The bridge's `now` block says only what changed since the agent's previous call." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end
local function empty(t) return type(t) == "table" and next(t) == nil end
local function wait_step(game) return game.server("return Mods.GateKitAI.AI.Step({ ['do'] = 'wait' })", 60) end

function run(game)
    game.wait_state("Running", "server", 120)
    local first = step("the whole picture", "cheat", function()
        return game.server("Mods.GateKitAI.AI.Session.Start({ mode = 'open', plan = 'off' }) return Mods.GateKitAI.AI.Look({})", 30)
    end)
    step("every standing part is there", "cheat", function()
        check(first.ok and first.did == "looked: all", "the look answered", first)
        for _, key in ipairs({ "where", "fight", "party", "waiting" }) do check(first.now[key] ~= nil, "the part " .. key, first.now) end
        eq(first.now.fight, "off", "no fight")
        check(contains(first.now.where, "Basic_Level_A"), "the level in where", first.now.where)
    end)
    local mark = game.mark()
    local a = step("a step with nothing before it", "cheat", function() return wait_step(game) end)
    local b = step("and another", "cheat", function() return wait_step(game) end)
    step("both blocks are empty, the answers whole", "cheat", function()
        check(a.ok and empty(a.now), "nothing changed: an empty block", a.now)
        check(b.ok and empty(b.now), "nothing changed again: an empty block", b.now)
        for _, key in ipairs({ "ok", "mode", "did", "why_not", "hint", "now" }) do check(b[key] ~= nil, "the answer's key " .. key, b) end
        local r = row(mark, "ai_step", function(p) return p["do"] == "wait" end, 5)
        check(type(r.payload.ticks) == "number" and type(r.payload.chars) == "number", "the row keeps ticks and chars", r.payload)
    end)
    local human = step("a human comes into view", "cheat", function()
        local g = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            return GK.Stage.Spawn{ template = "human", at = { x + 4, y, z + 3 }, faction = "neutral" }[1] ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        return uuid(g)
    end)
    local c = step("the step after", "cheat", function() return wait_step(game) end)
    local d = step("and the one after that", "cheat", function() return wait_step(game) end)
    step("the human once, as new, by its handle", "cheat", function()
        local line = c.now.near and c.now.near[1]
        check(line and contains(line, "+ ") and contains(line, "#" .. human:sub(1, 6)), "the human in near, marked new", c.now)
        check(contains(line, "neutral"), "how it stands to the party", line)
        check(empty(d.now), "not said twice", d.now)
        local cap = game.server("return Mods.GateKitAI.AI.Limit.chars")
        check(#game.server("return Mods.GateKitAI.AI.Encode((Mods.GateKitAI.AI.Now({ full = true, peek = true })))") <= cap, "the whole picture under the hard cap", cap)
    end)
    step("the handle names it", "cheat", function()
        eq(uuid(game.server(string.format("return Mods.GateKitAI.AI.Resolve(%q)", "#" .. human:sub(1, 6)))), human, "the handle resolves to the human")
    end)
end
