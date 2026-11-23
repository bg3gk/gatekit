-- Scenes, the plan and notes in the bridge (mods/AI/README.md). The rule held here: any table
-- under the global GKScene is a scene with no name known to the mod - its state appears in
-- `now.scenes` when it is made and again when it changes, its functions are calls AI.Scene runs by
-- name, a call it has not is refused with the calls it has; a loaded plan's next step is
-- `now.route`, `do = plan` does the step's act, and the step is passed when its condition of
-- arrival is true in the game; a note written at a place is kept with it, not told back to its
-- writer at once, and told to a session that comes there anew.
-- Staging (cheat): a scene table of the test's own, a plan of two walks, one note.
-- Run: gatekit --instance <copy> test tests -k ai/scenes --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A scene's state is reported with no name hard-wired; a plan gives the route; a note comes back." }

local function has(list, text)
    for _, line in ipairs(list or {}) do if contains(line, text) then return line end end
end
local function call(game, body, timeout) return game.server("return Mods.GateKitAI.AI.Step({ " .. body .. " })", timeout or 90) end

function run(game)
    game.wait_state("Running", "server", 120)
    step("the picture before", "cheat", function()
        game.server("local AI = Mods.GateKitAI.AI; AI.Session.Start({ mode = 'open', plan = 'off' }) AI.Notes.Clear() return AI.Look({})", 30)
    end)
    step("a scene of the test's own is armed", "cheat", function()
        game.server([[
            GKScene = GKScene or {}
            GKScene.ai_probe = { armed = true, ready = false, stage = "one", who = { guest = { seen = true } },
                go = function() GKScene.ai_probe.stage = "two"; GKScene.ai_probe.done = true; return true end,
                early = function() return { ok = false, error = "not before the end", hint = "wait for done" } end }
            return true ]])
    end)
    local armed = step("the step after", "cheat", function() return call(game, "['do'] = 'wait'") end)
    step("its state is in scenes", "cheat", function()
        local line = has(armed.now.scenes, "ai_probe:")
        check(line and contains(line, "armed") and contains(line, "stage=one") and contains(line, "who.guest.seen"), "the scene with its flags and fields", armed.now)
        check(not contains(line, "ready"), "a false flag is not said", line)
    end)
    local went = step("its call is run by name", "cheat", function()
        return game.server("return Mods.GateKitAI.AI.Scene({ name = 'ai_probe', call = 'go' })", 30)
    end)
    step("the call ran and the state changed", "cheat", function()
        check(went.ok and contains(went.did, "ai_probe.go()"), "the call ran", went)
        local line = has(went.now.scenes, "ai_probe:")
        check(line and contains(line, "stage=two") and contains(line, "done"), "the new state in scenes", went.now)
        local no = game.server("return Mods.GateKitAI.AI.Scene({ name = 'ai_probe', call = 'fly' })", 30)
        check(no.ok == false and contains(no.why_not, "no call fly") and contains(no.hint, "go"), "a call it has not is refused with those it has", no)
        local early = game.server("return Mods.GateKitAI.AI.Scene({ name = 'ai_probe', call = 'early' })", 30)
        check(early.ok == false and contains(early.why_not, "not before the end") and contains(early.hint, "wait for done"), "a call the scene refuses is refused with its reason and hint", early)
    end)
    step("the scene is taken away", "cheat", function()
        game.server("GKScene.ai_probe = nil return true")
        local gone = call(game, "['do'] = 'wait'")
        check(has(gone.now.scenes, "ai_probe: gone"), "its going is said once", gone.now)
    end)

    -- a stand arms most scenes through the mod, in GateKit's environment, and a few with
    -- `gatekit exec`, in the global one: both are one table of scenes
    local both = step("a scene armed in GateKit's environment, then one by exec", "cheat", function()
        -- no table of scenes anywhere, so that the first is made in GateKit's environment
        game.server([[
            GKScene = nil
            rawset(Mods.GateKit, "GKScene", nil)
            -- Script Extender's load takes the environment second, as the AI mod runs a scene
            load("GKScene = GKScene or {} GKScene.ai_own = { armed = true, stage = 'own' } return true", Mods.GateKit)()
            return true ]])
        game.server("GKScene = GKScene or {} GKScene.ai_exec = { armed = true, stage = 'exec' } return true")
        return call(game, "['do'] = 'wait'")
    end)
    step("both are scenes", "cheat", function()
        check(has(both.now.scenes, "ai_own:") and has(both.now.scenes, "stage=own"), "the scene of GateKit's environment", both.now)
        check(has(both.now.scenes, "ai_exec:") and has(both.now.scenes, "stage=exec"), "the scene armed by exec", both.now)
        local again = game.server("return Mods.GateKitAI.AI.Scene({ name = 'ai_exec' })", 30)
        check(again.ok, "the scene armed by exec is read by name", again)
        game.server("GKScene.ai_own = nil GKScene.ai_exec = nil return true")
    end)

    local from = game.server("local x, y, z = Osi.GetPosition(Osi.GetHostCharacter()); return { x, y, z }")
    local loaded = step("a plan of two walks", "cheat", function()
        return game.server(string.format([[
            Mods.GateKitAI.AI.Plan.Load({ name = "probe", steps = {
                { goal = "Walk to the first post", ["do"] = "go", target = "%f,%f,%f", arrive = { near = { %f, %f, %f }, within = 2 } },
                { goal = "Walk back", ["do"] = "go", target = "%f,%f,%f", arrive = { near = { %f, %f, %f }, within = 2 } } } })
            local shown = Mods.GateKitAI.AI.Look({ part = "plan" })
            shown.now.route = Mods.GateKitAI.AI.Look({ part = "route" }).now.route
            return shown ]],
            from[1] + 6, from[2], from[3], from[1] + 6, from[2], from[3], from[1], from[2], from[3], from[1], from[2], from[3]), 30)
    end)
    step("the route names the first step", "cheat", function()
        check(loaded.ok and contains(loaded.now.route, "1/2 Walk to the first post") and contains(loaded.now.route, "do=plan"), "the route", loaded.now)
        eq(#loaded.now.plan, 2, "both steps shown")
    end)
    local first = step("the plan's act, with a note left here", "cheat", function()
        return call(game, "['do'] = 'plan', note = 'the post is by the east wall'")
    end)
    step("the walk was done and the route moved on", "cheat", function()
        check(first.ok and contains(first.did, "walked to"), "the plan's walk", first)
        check(first.now.route and contains(first.now.route, "2/2 Walk back"), "the next step in route", first.now)
    end)
    local back = step("the second step", "cheat", function() return call(game, "['do'] = 'plan'") end)
    step("the plan is done, and the note is told on coming back", "cheat", function()
        check(back.ok and contains(back.now.route, "the plan is done"), "the plan's end", back.now)
        check(first.now.notes == nil and back.now.notes == nil, "the note is not told back to the one who has just written it", first.now, back.now)
        local kept = game.server("return Mods.GateKitAI.AI.Look({ part = 'notes' })", 30)
        check(has(kept.now.notes, "the post is by the east wall"), "the note is kept with the level", kept.now)
        local again = game.server("Mods.GateKitAI.AI.Sessions.fresh = nil return (Mods.GateKitAI.AI.Now({ session = 'fresh' }))", 30)
        check(has(again.notes, "the post is by the east wall"), "a session that comes here anew is told it", again)
        game.server("Mods.GateKitAI.AI.Plan.Off() return Mods.GateKitAI.AI.Notes.Clear()")
    end)
end
