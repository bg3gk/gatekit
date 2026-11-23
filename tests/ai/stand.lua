-- A stand's scenes as data (mods/AI/README.md, "Scenes"), and the window act (`do = ui`). The rule
-- held here: a scene the manifest names is armed by name - from its file's code the first time,
-- refused with the call that names the file when no code is kept, and from the kept code after
-- that - with the manifest's options set before its code, and disarmed by name; the mod lists the
-- manifest's scenes it has not armed with that call; a call of a scene of the client's, made on
-- the server, is run on the client and what it answered is said; `do = ui` lists an open window's controls
-- by handle and refuses a window that is not open.
-- Staging (cheat): a manifest and a scene of the test's own.
-- Run: gatekit --instance <copy> test tests -k ai/stand --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A manifest's scene is armed by name, from a file once and from the kept code after; ui reads a window." }

local MANIFEST = [[{ "scenes": { "ai_stand_probe": { "side": "server", "arm": "probe.lua", "off": "probe_off.lua",
    "options": { "ai_stand_probe": { "n": 7 } } } } }]]
local ARM = [[GKScene = GKScene or {} GKScene.ai_stand_probe = { armed = true, n = GKSceneOptions.ai_stand_probe.n } return { armed = true }]]
local OFF = [[GKScene.ai_stand_probe = nil return { ok = true }]]

local function scene(game, args) return game.server("return Mods.GateKitAI.AI.Scene(" .. args .. ")", 30) end
local function has(list, text)
    for _, line in ipairs(list or {}) do if contains(line, text) then return line end end
end

function run(game)
    game.wait_state("Running", "server", 120)
    step("the manifest is given", "cheat", function()
        -- the copy keeps the code of an earlier run's arming: none kept, as in a copy never armed
        game.server("Mods.GateKitAI.AI.Stand.Keep('ai_stand_probe', 'arm', '') Mods.GateKitAI.AI.Stand.Keep('ai_stand_probe', 'off', '') return true")
        local began = game.server(string.format("return Mods.GateKitAI.AI.SessionCall({ mode = 'open', plan = 'off', scenes = %q, scenes_from = 'C:/stand/scenes/stand.json' })", MANIFEST), 30)
        check(began.ok, "the session took the manifest", began)
    end)
    local listed = step("its scene is listed with the call that arms it", "cheat", function() return scene(game, "{}") end)
    step("the list names the file", "cheat", function()
        local line = has(listed.now.scenes, "ai_stand_probe (server): not armed")
        check(line and contains(line, '"file":"C:/stand/scenes/probe.lua"'), "the arming call with the file beside the manifest", listed.now)
    end)
    local bare = step("arming with no code kept", "cheat", function() return scene(game, "{ name = 'ai_stand_probe', call = 'arm' }") end)
    step("is refused with the call that names the file", "cheat", function()
        check(bare.ok == false and contains(bare.why_not, "not kept yet") and contains(bare.hint, "probe.lua"), "the refusal and its hint", bare)
    end)
    local armed = step("arming with the file's code", "cheat", function()
        return scene(game, string.format("{ name = 'ai_stand_probe', call = 'arm', code = %q }", ARM))
    end)
    step("the scene stands, with the manifest's options", "cheat", function()
        check(armed.ok and contains(armed.did, "ai_stand_probe armed (server)"), "armed", armed)
        eq(game.server("return GKScene.ai_stand_probe and GKScene.ai_stand_probe.n"), 7, "the option was set before the code")
        game.server("GKScene.ai_stand_probe = nil return true")
    end)
    local again = step("arming again by name alone", "cheat", function() return scene(game, "{ name = 'ai_stand_probe', call = 'arm' }") end)
    step("runs the kept code", "cheat", function()
        check(again.ok, "armed from the kept code", again)
        eq(game.server("return GKScene.ai_stand_probe and GKScene.ai_stand_probe.n"), 7, "the scene stands again")
    end)
    local off = step("disarming with the off file's code", "cheat", function()
        return scene(game, string.format("{ name = 'ai_stand_probe', call = 'off', code = %q }", OFF))
    end)
    step("the scene is gone and no longer armed", "cheat", function()
        check(off.ok and contains(off.did, "disarmed"), "disarmed", off)
        eq(game.server("return GKScene.ai_stand_probe == nil"), true, "its table is gone")
        eq(game.server("return Mods.GateKitAI.AI.Stand.Armed().ai_stand_probe == nil"), true, "the session forgot it")
    end)

    local called = step("a call of a scene of the client's, made on the server", "cheat", function()
        game.ui([[GKScene = GKScene or {} GKScene.ai_client_probe = { armed = true,
            look = function() return { answer = "the hotbar is " .. (GK.Gui.Widget('HotBar') and "open" or "closed") } end,
            no = function() return { ok = false, error = "not now" } end,
            later = function() return GK.Observe({ ok = true }, function(ticks) if ticks < 5 then return nil end return "after five ticks" end, 3000) end } return true]])
        game.wait("server", "Mods.GateKitAI.AI.Client.scenes.ai_client_probe ~= nil", 10)
        return scene(game, "{ name = 'ai_client_probe', call = 'look' }")
    end)
    step("is run on the client, where the windows are, and its answer is said", "cheat", function()
        check(called.ok and contains(called.did, "ai_client_probe.look() answered the hotbar is open"), "the answer", called)
        local no = scene(game, "{ name = 'ai_client_probe', call = 'no' }")
        check(no.ok == false and contains(no.why_not, "not now"), "the call's refusal is the step's", no)
        local later = scene(game, "{ name = 'ai_client_probe', call = 'later' }")
        check(later.ok and contains(later.did, "answered after five ticks"), "a call that answers an observation is followed to its end", later)
        game.ui("GKScene.ai_client_probe = nil return true")
    end)

    local looked = step("ui lists the controls of an open window", "cheat", function()
        return game.server("return Mods.GateKitAI.AI.Step({ ['do'] = 'ui', window = 'HotBar' })", 60)
    end)
    step("each control by handle", "cheat", function()
        check(looked.ok and type(looked.now.window) == "table" and #looked.now.window > 0, "the window's controls", looked)
    end)
    local closed = step("ui on a window that is not open", "cheat", function()
        return game.server("return Mods.GateKitAI.AI.Step({ ['do'] = 'ui', window = 'NoSuchWindow' })", 60)
    end)
    step("is refused, saying so", "cheat", function()
        check(closed.ok == false and contains(closed.why_not, "no window NoSuchWindow is open"), "the refusal", closed)
        game.server("Mods.GateKitAI.AI.SessionFile.Write({ stand = false, armed = false }) return true")
    end)
end
