-- AI.Session and AI.Mode (mods/AI/README.md): a session's mode and the owner's prompt. The rule
-- held here: every answer names its mode; in mode blind a creature the party does not perceive is
-- in no part of a look, an act at it by name or by handle is refused as not knowable to a player,
-- a roll the game shows no window for is not told while the hit it decided is, and the scenes, the
-- route and the plan's act are refused; the same asks answer in mode open - the creature listed
-- as unseen, the act begun, the roll told. The owner's prompt is answered by the session's start
-- and by a look that asks for it, a whole look only reminds of it, and a step's block never holds it.
-- Staging (cheat): a hostile human beside the host, hit by script (GK.Cheat.Attack); the creature
-- not perceived is one of the level's own, far off. Every call of the bridge writes its row, so
-- the steps that call it are cheat steps.
-- Run: gatekit --instance <copy> test tests -k ai/session --speed working

test { level = "GustavX:Basic_Level_A",
       about = "Blind mode tells only what a player could know; the owner's prompt is handed over at the session's start." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end
local function has(list, text)
    for _, line in ipairs(list or {}) do if contains(line, text) then return line end end
end
local function ai(game, code, timeout) return game.server("local AI = Mods.GateKitAI.AI; " .. code, timeout or 60) end

function run(game)
    game.wait_state("Running", "server", 120)
    local far = step("a creature of the level the party does not perceive", "cheat", function()
        local g = game.server([[
            Mods.GateKitAI.AI.Detail.full.radius = 95
            for _, c in ipairs(GK.See.Around({ radius = 90, scope = "all", limit = 60 }).characters) do
                if c.perceived == false and not c.dead then return c.id end
            end ]])
        check(g, "the level has one within 90 m")
        check(not game.server(string.format("return GK.See.PartyPerceives(%q)", g)), "the party does not perceive it")
        return uuid(g)
    end)
    local handle = "#" .. far:sub(1, 6)

    local began = step("a session in mode open, with a prompt", "cheat", function()
        return ai(game, "return AI.SessionCall({ mode = 'open', prompt = 'You are a wary elf. Say little.' })")
    end)
    step("the start hands the prompt over, the mode is named", "cheat", function()
        check(began.ok and began.mode == "open", "the mode in the answer", began)
        eq(began.prompt, "You are a wary elf. Say little.", "the prompt in the start's answer")
        check(began.now.where and began.now.party, "the whole picture with it", began.now)
        local look = ai(game, "return AI.Look({})")
        check(look.now.prompt and contains(look.now.prompt, "ai_look") and not contains(look.now.prompt, "wary elf"), "a whole look reminds of it in a line", look.now)
        eq(ai(game, "return AI.Look({ part = 'prompt' })").prompt, "You are a wary elf. Say little.", "a look that asks for it reads it")
        local waited = ai(game, "return AI.Step({ ['do'] = 'wait' })")
        check(waited.mode == "open" and waited.prompt == nil and waited.now.prompt == nil, "a step's answer never holds it", waited)
    end)
    step("open: the unperceived human is listed and can be named", "cheat", function()
        local look = ai(game, "return AI.Look({ part = 'near', detail = 'full' })")
        local line = has(look.now.near, handle)
        check(line and contains(line, "unseen"), "listed, as unseen", look.now)
        eq(uuid(ai(game, string.format("return AI.Resolve(%q)", handle))), far, "its handle resolves")
    end)
    -- a hostile human beside the host, hit by the host by script: one for each hit (a hit may kill)
    local function hit()
        local enemy = uuid(game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            return GK.Stage.Spawn{ template = "human", at = { x + 1.5, y, z }, faction = "hostile" }[1] ]]))
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", enemy), 10)
        local mark = game.mark()
        game.server(string.format("return GK.Cheat.Attack(Osi.GetHostCharacter(), %q)", enemy), 30)
        row(mark, "game_hit", function(p) return uuid(p.who) == enemy end, 20)
    end
    step("open: a roll with no window is told", "cheat", function()
        ai(game, "return AI.Look({})")
        hit()
        local now = ai(game, "return (AI.Now({ peek = true }))")
        check(now.rolls and #now.rolls > 0, "the attack's roll in rolls", now)
        check(now.events and #now.events > 0, "the hit in events", now)
    end)

    step("the session goes blind: the same hit is told, its hidden roll is not", "cheat", function()
        local blind = ai(game, "return AI.SessionCall({ mode = 'blind' })")
        check(blind.ok and blind.mode == "blind", "the mode in the answer", blind)
        eq(blind.prompt, "You are a wary elf. Say little.", "the prompt stays")
        check(blind.now.rolls == nil, "no rolls", blind.now)
        check(blind.now.events and #blind.now.events > 0, "the hit in events", blind.now)
    end)
    step("blind: the unperceived human is nowhere, and cannot be named", "cheat", function()
        local look = ai(game, "return AI.Look({ detail = 'full' })")
        eq(look.mode, "blind", "the look names the mode")
        check(not has(look.now.near, handle), "not in near", look.now.near)
        check(not has(ai(game, "return AI.Look({ part = 'things', detail = 'full' })").now.things, handle), "not in things")
        local byHandle = ai(game, string.format("return AI.Step({ ['do'] = 'talk', target = %q })", handle))
        check(byHandle.ok == false and contains(byHandle.why_not, "not knowable to a player"), "an act at it by handle is refused", byHandle)
        local byId = ai(game, string.format("return AI.Step({ ['do'] = 'go', target = %q })", far))
        check(byId.ok == false and contains(byId.why_not, "not knowable to a player"), "and by its uuid", byId)
    end)
    step("blind: scenes, the route and the plan's act are refused", "cheat", function()
        game.server("GKScene = GKScene or {} GKScene.ai_probe = { armed = true, go = function() GKScene.ai_probe.went = true end } return true")
        local now = ai(game, "return (AI.Now({ full = true, peek = true }))")
        check(now.scenes == nil and now.route == nil, "neither in the block", now)
        for _, part in ipairs({ "scenes", "route", "plan" }) do
            local no = ai(game, string.format("return AI.Look({ part = %q })", part))
            check(no.ok == false and contains(no.why_not, "not knowable to a player"), "the part " .. part .. " is refused", no)
        end
        local plan = ai(game, "return AI.Step({ ['do'] = 'plan' })")
        check(plan.ok == false and contains(plan.why_not, "not knowable to a player"), "the plan's act is refused", plan)
        local start = ai(game, "return AI.SessionCall({ plan = 'prologue' })")
        check(start.ok == false and contains(start.why_not, "not knowable to a player"), "a plan cannot be loaded", start)
        local list = ai(game, "return AI.Scene({})")
        check(list.ok == false and contains(list.why_not, "not knowable to a player"), "the scenes are not listed", list)
        local ran = ai(game, "return AI.Scene({ name = 'ai_probe', call = 'go' })")
        check(ran.ok and ran.now.scenes == nil and game.server("return GKScene.ai_probe.went == true"), "a call the owner names still runs, and nothing of the scene comes back", ran)
        game.server("GKScene.ai_probe = nil return true")
    end)
    step("the session is put back", "cheat", function()
        local open = ai(game, "AI.Detail.full.radius = 40 return AI.SessionCall({ mode = 'open', prompt = '' })")
        check(open.ok and open.mode == "open" and open.prompt == "", "open, no prompt", open)
    end)
end
