-- A mod disabled through Ext.Mod.SetEnabled in the main menu is not loaded at the next session's
-- start, and is loaded again once enabled again (the fork's Docs/API.md, "Mods - Ext.Mod").
-- Ext.Mod.SetEnabled makes the calls of the game's own mod manager page:
-- code:ecl::ModManagerClient::RemoveMod (after code:ls::ModuleSettings::RemoveMod) and
-- code:ecl::ModManagerClient::AddMod, each of which calls
-- code:ecl::ModManagerClient::SaveSettingsAndReload: code:ls::ModuleSettings::ResolveAddonsOrder,
-- modsettings.lsx written, and the module list resolved again at once
-- (code:ls::ModManager::LoadModFromSettings, which clears the manager's byte +0x150: Ext.Mod.Pending()). The game loads its modules again at
-- the next session's start: the client's UnloadModule and LoadModule. The mod's loading-screen hint
-- follows that reload (the fork's "Loading Hints").
-- The mod is the bridge's GateKit AI, enabled in every copy, which no mod depends on; its hint file
-- is put loose under the copy's Data/Public before the start (`files`).
-- Venue: a fresh level (a quick save there is the session the menu's Continue loads again).
-- Classes: the quick save, the way back to the menu, Continue and the answers to the game's checks
-- of the save's mods (their own buttons) are the player's own moves (fair); Ext.Mod.SetEnabled is the
-- game's mod manager's own toggle without its page (fair); the lists, the timeline and the event
-- log are read (observation). No step waits for time: each waits for the row the game's answer makes.
-- Run: gatekit --instance <copy> test tests -k startup/mods/ext_mod

test { level = "GustavX:Basic_Level_A", surprises = { "box", "saving", "loading", "splash" },
       files = { ["Data/Public/Gwynerva_GateKitAI/Hints/Hints.lsx"] = "ai.lsx" },
       about = "A mod disabled through Ext.Mod.SetEnabled in the main menu is not loaded at the next session's start, its hint gone; enabled again, it is back." }

local AI = "a1c0de57-6b1d-4a17-9e2b-0a1b7e57a11e"   -- GateKit AI
local KEY = "GK_HINT_F9_AI"

-- The game's mod list and hint list now, as Ext.Mod and the hint composition give them.
local READ = [[
    local entry
    for _, e in ipairs(Ext.Mod.GetList()) do if e.UUID == "]] .. AI .. [[" then entry = e end end
    local has = false
    for _, h in ipairs(GK.Hints.List() or {}) do if h.key == "]] .. KEY .. [[" then has = true end end
    local p = Ext.Mod.Pending()
    return { enabled = entry and entry.Enabled, loaded = entry and entry.Loaded, source = entry and entry.Source,
             requiredBy = entry and #entry.RequiredBy, aiLoaded = Ext.Mod.IsModLoaded("]] .. AI .. [["),
             pending = p.Pending, byte = p.Byte, since = p.Since, added = p.Added, removed = p.Removed,
             has = has, moment = Ext.Hints.GetComposition().Moment }
]]

local function read(game, name)
    local r = game.client(READ, 60)
    game.save(name .. ".txt", fmt(r) .. "\n")
    return r
end

local function set_enabled(game, on, name)
    local r = game.client("return Ext.Mod.SetEnabled('" .. AI .. "', " .. tostring(on) .. ")", 60)
    game.save(name .. ".txt", fmt(r) .. "\n")
    return r
end

-- The timeline's client UnloadModule/LoadModule states and ModuleList rows since row `since`.
local function rows(game, since)
    return game.client([[
        local out = {}
        for _, e in ipairs(Ext.StartupVideos.Timeline(]] .. tostring(since or 0) .. [[)) do
            if e.What == "ModuleList" then
                out[#out + 1] = { what = e.What, name = e.Name, moment = e.Moment, modules = e.Modules }
            elseif e.What == "State" and e.Side == "Client" and (e.To == "UnloadModule" or e.To == "LoadModule") then
                out[#out + 1] = { what = "State", to = e.To }
            end
        end
        return out
    ]], 60)
end

local function last_row(game)
    return game.client("local t = Ext.StartupVideos.Timeline() return t.Count", 30)
end

-- The client's game_state rows of the event log since the cursor `since`.
local function states(game, since)
    local out = {}
    for _, r in ipairs(game.events(since, { "game_state" }).rows or {}) do
        local p = r.payload or {}
        out[#out + 1] = { side = r.side, from = p.from, to = p.to }
    end
    return out
end

-- The "press any key" page takes its key once it has been entered and its fade-in is over (the
-- start's timeline after row `row`: its UIState and Fade rows), as in test:startup/mods/hints_follow.
local function splash_ready(row)
    return [[(function()
        local entered = false
        for _, r in ipairs(Ext.StartupVideos.Timeline(]] .. tostring(row) .. [[) or {}) do
            if r.What == "UIState" and r.Name == "SplashScreen" and r.Change == "Entered" then entered = true
            elseif entered and r.What == "Fade" and r.To == "None" then return true end
        end
        return false
    end)()]]
end

-- The main menu's page is ready for a click once its layout holds its buttons.
local MENU_READY = [[(function()
    local l = GK.Gui.Look("MainMenu", { layout = true })
    if not l.laidOut then return false end
    for _, c in ipairs(l.controls or {}) do if c.handle == "ModsButton" then return true end end
    return false
end)()]]

-- To the main menu from the session, past its "press any key" page; each move waits for the row
-- the game's answer makes.
local function to_menu(game)
    local since, row = game.mark(), last_row(game)
    game.key("escape")
    game.wait_row(since, { "menu" }, function(p) return p.window == "gamemenu" end, 10)
    check(game.ui("return GK.Gui.Click('GameMenu', 'MainMenuButton')").ok, "Main Menu clicked")
    check(game.ui("return GK.Gui.Resolve{ box = { leave = true } }").ok, "the box's yes")
    game.wait_attention({ "splash" }, 180, since)
    need(game.wait("client", splash_ready(row), 30), "the press-any-key page's fade-in is over")
    local key = game.mark()
    game.key("space")
    game.wait_attention({ "splash_ended" }, 30, key)
    game.wait_row(key, { "ui_page" }, function(p) return p.page == "MainMenu" end, 30)
    need(game.wait("ui", MENU_READY, 30), "the main menu's buttons are made and laid out")
end

-- The message box's button bound to UIAccept ("yes"), clicked.
local BOX_YES = [[
    local summary = (GK.Gui.Blocking() or {}).summary or {}
    local n
    for _, b in ipairs(summary.buttons or {}) do if b.event == "UIAccept" then n = b.n end end
    local buttons = {}
    for _, c in ipairs(GK.Gui.Look("Dialog_box").controls or {}) do
        if c.kind == "button" then buttons[#buttons + 1] = c end
    end
    if not (n and buttons[n]) then return { ok = false, error = "no UIAccept button", summary = summary } end
    return GK.Gui.Click("Dialog_box", buttons[n].handle)
]]

-- Continue: the quick save is loaded; the game's checks of the save's mods (the page
-- ModVerification, then the box that warns of the mods disabled) answered "go on" through their own
-- buttons as they come, until the load ends.
local function continue(game, name)
    local since = game.mark()
    check(game.ui([[ return GK.Gui.Click("MainMenu", "ContinueButton").ok ]]), "Continue ran")
    local answered = {}
    while true do
        local row = game.wait_row(since, { "ui_page", "box", "loading_ended" },
            function(p, r) return r.kind ~= "ui_page" or p.page == "ModVerification" end, 300)
        since = game.mark()
        if row.kind == "loading_ended" then break end
        local which = row.kind == "box" and "box" or "ModVerification"
        local ok, answer = pcall(game.ui, which == "box" and BOX_YES
            or [[ return GK.Gui.Click("ModVerification", "StartGameButton") ]])
        answered[#answered + 1] = { window = which, answer = ok and answer or tostring(answer) }
        if ok then check(answer.ok, which .. " answered through its own button", answer) end
    end
    game.save(name .. "-continue.txt", fmt({ answered = answered }) .. "\n")
    game.wait_state("Running", "client", 300)
end

-- After a load: the client's UnloadModule and LoadModule both came (event log and timeline), and the
-- module list the client loaded differs from the one before.
local function check_reload(game, name, mark, since)
    local s, t = states(game, mark), rows(game, since)
    game.save(name .. "-rows.txt", fmt({ game_state = s, timeline = t }) .. "\n")
    local unload, load, differs = false, false, false
    for _, x in ipairs(t) do
        if x.what == "State" and x.to == "UnloadModule" then unload = true end
        if x.what == "State" and x.to == "LoadModule" then load = true end
        if x.what == "ModuleList" and x.moment == "LoadModule" and contains(x.name or "", "differs") then differs = true end
    end
    check(unload and load, "the client went through UnloadModule and LoadModule (the timeline)", t)
    check(differs, "the list the client loaded differs from the one before (the timeline's ModuleList)", t)
    -- The client's Lua is made anew inside LoadModule, so its first game_state row is the one
    -- that leaves it.
    local gsLoad = false
    for _, x in ipairs(s) do if x.side == "client" and x.from == "LoadModule" then gsLoad = true end end
    check(gsLoad, "a game_state row of the client leaving LoadModule", s)
end

function run(game)
    game.wait_state("Running", "client", 120)
    step("the start loaded the mod and composed its hint", "observation", function()
        local r = read(game, "start")
        eq(r.aiLoaded, true, "GateKit AI is loaded")
        eq(r.source, "Settings", "GateKit AI is in the settings")
        eq(r.requiredBy, 0, "no loaded module depends on GateKit AI")
        check(r.has, KEY .. " is in the game's list", r)
    end)
    step("a quick save", "fair", function()
        local since = game.mark()
        check(game.ui("return GK.Gui.QuickSave()").ok, "the quick save ran")
        game.wait_attention({ "saving_ended" }, 60, since)
    end)
    step("the main menu", "fair", function() to_menu(game) end)
    step("Ext.Mod.SetEnabled off: the settings lose the mod, the reload waits", "fair", function()
        local before = read(game, "menu-before")
        eq(before.pending, false, "nothing waits in the menu after a session")
        local r = set_enabled(game, false, "disable")
        check(r.Done, "SetEnabled(off) was done", r)
        check(has(r.Removed, AI) and not has(r.Settings, AI), "the settings lost GateKit AI", r)
        check(not has(r.Loaded, AI), "the resolved list lost GateKit AI at once", r)
        eq(r.Pending.Pending, true, "the reload waits for the next session's start")
        check(has(r.Pending.Removed, AI), "the waiting change removes GateKit AI", r.Pending)
        local again = set_enabled(game, false, "disable-again")
        eq(again.Done, false, "a second SetEnabled(off) is refused")
    end)
    local mark, since = game.mark(), last_row(game)
    step("the save loaded", "fair", function() continue(game, "disabled") end)
    step("the game reloaded its modules without the mod; its hint is gone", "observation", function()
        check_reload(game, "disabled", mark, since)
        local r = read(game, "disabled")
        eq(r.aiLoaded, false, "GateKit AI is not loaded")
        eq(r.enabled, false, "GateKit AI is not in the settings")
        eq(r.has, false, KEY .. " left the game's list")
        eq(r.pending, false, "nothing waits after the reload")
    end)
    step("the main menu again", "fair", function() to_menu(game) end)
    step("Ext.Mod.SetEnabled on: the settings get the mod back, the reload waits", "fair", function()
        local r = set_enabled(game, true, "enable")
        check(r.Done, "SetEnabled(on) was done", r)
        check(has(r.Added, AI) and has(r.Settings, AI) and has(r.Loaded, AI), "the settings and the resolved list hold GateKit AI", r)
        eq(r.Pending.Pending, true, "the reload waits for the next session's start")
        check(has(r.Pending.Added, AI), "the waiting change adds GateKit AI", r.Pending)
    end)
    mark, since = game.mark(), last_row(game)
    step("the save loaded again", "fair", function() continue(game, "enabled") end)
    step("the game reloaded its modules with the mod; its hint is back", "observation", function()
        check_reload(game, "enabled", mark, since)
        local r = read(game, "enabled")
        eq(r.aiLoaded, true, "GateKit AI is loaded again")
        eq(r.source, "Settings", "GateKit AI is in the settings")
        check(r.has, KEY .. " is back in the game's list", r)
        eq(r.pending, false, "nothing waits after the reload")
    end)
end
