-- The loading screen's hints follow the game's reload of its modules: a mod disabled in the game's
-- own mod manager loses its hints at the next session's start, and gets them back when enabled
-- again (the fork's Docs/API.md, "Loading Hints", "Which modules").
-- The game applies a toggle of its mod manager at the next session's start: the client's
-- UnloadModule and LoadModule (code:ecl::ModManagerClient::SaveSettingsAndReload re-resolves the
-- list at once through code:ls::ModManager::LoadModFromSettings; nothing is reloaded until a
-- session starts). At the end of that LoadModule the fork compares the modules the game loaded with
-- the ones the hint list was composed from, composes again - the game's own Hints.lsx loaded anew
-- by code:ls::FileFormatIO::LoadFile, then every module's file of the new list - and puts the new
-- list into code:ecl::HintManager::m_ptr on the interface's thread.
-- The mod is the bridge's GateKit AI, enabled in every copy; its hint file is put loose under the
-- copy's Data/Public before the start (`files`: the linked Data/Public is made a real folder for it).
-- Venue: a fresh level (a quick save there is the session the menu's Continue loads again); the
-- toggles are the mod manager's own check boxes (GK.Gui on the ModBrowser pages). Classes: the
-- quick save, the way back to the menu (Escape, the game menu's Main Menu, the box's yes, the
-- page's key), the toggles, the answers to the game's checks of the save's mods (their own
-- buttons) and the loads are the player's own moves (fair); the list is read (observation). No
-- step waits for time: each waits for the row the game's answer makes.
-- Run: gatekit --instance <copy> test tests -k startup/mods/hints_follow

test { level = "GustavX:Basic_Level_A", surprises = { "box", "saving", "loading", "splash" },
       files = { ["Data/Public/Gwynerva_GateKitAI/Hints/Hints.lsx"] = "ai.lsx" },
       about = "A mod disabled in the game's mod manager loses its loading-screen hints at the next session's module reload, and an enabled one gets them back." }

local AI = "a1c0de57-6b1d-4a17-9e2b-0a1b7e57a11e"   -- GateKit AI
local KEY = "GK_HINT_F8_AI"

-- The game's hint list now: whether KEY is in it, with the composition's own account.
local READ = [[
    local list = GK.Hints.List() or {}
    local at
    for _, h in ipairs(list) do if h.key == "]] .. KEY .. [[" then at = h end end
    local c = Ext.Hints.GetComposition()
    local loaded = {}
    for _, m in ipairs(Ext.Mod.GetLoadOrder()) do loaded[#loaded + 1] = m end
    return { count = #list, has = at ~= nil, source = at and at.source, moment = c.Moment,
             compositions = c.Compositions, pending = c.Pending, gameCount = c.GameCount,
             listSource = c.ModuleList and c.ModuleList.Source, aiLoaded = Ext.Mod.IsModLoaded("]] .. AI .. [["),
             loaded = #loaded }
]]

local function read(game, name)
    local r = game.client(READ, 60)
    game.save(name .. ".txt", fmt(r) .. "\n")
    return r
end

-- The timeline's rows of the module lists and the hints' compositions since row `since`.
local function rows(game, since)
    return game.client([[
        local out = {}
        for _, e in ipairs(Ext.StartupVideos.Timeline(]] .. tostring(since or 0) .. [[)) do
            if e.What == "ModuleList" or e.What == "HintsComposed" then
                out[#out + 1] = { row = e.Row, ms = math.floor(e.Ms), what = e.What, name = e.Name, moment = e.Moment,
                                  modules = e.Modules, count = e.Count, gameCount = e.GameCount }
            elseif e.What == "State" and e.Side == "Client" and (e.To == "UnloadModule" or e.To == "LoadModule") then
                out[#out + 1] = { row = e.Row, ms = math.floor(e.Ms), what = "State", to = e.To }
            end
        end
        return out
    ]], 60)
end

local function last_row(game)
    return game.client("local t = Ext.StartupVideos.Timeline() return t.Count", 30)
end

-- The "press any key" page takes its key once it has been entered and the client's fade-in after
-- it is over (a key sent while it fades in was dropped once and the page stayed): the start's
-- timeline after row `row` (its UIState and Fade rows), the same reading as SPLASH_READY of
-- tests/startup/menu/_menu.lua, there over the whole timeline.
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

-- The main menu's page is laid out some frames before its buttons are made (seen: laid out with no
-- control at the page's `ui_page` row, ModsButton and the others about 20 frames later): the page
-- is ready for a click once the layout has arranged it with its buttons in it.
local MENU_READY = [[(function()
    local l = GK.Gui.Look("MainMenu", { layout = true })
    if not l.laidOut then return false end
    for _, c in ipairs(l.controls or {}) do if c.handle == "ModsButton" then return true end end
    return false
end)()]]

-- To the main menu from the session, past its "press any key" page. Each move waits for the row the
-- game's answer makes: the game menu's `menu` row, the `splash` row of the page (the load to the menu
-- resets the client's Lua; the event log's stream goes on through it), the page's fade-in in the
-- timeline, the page's `splash_ended`, the main menu's `ui_page` and its layout.
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

-- The mod manager's Installed page: GateKit AI's check box clicked (its place in the page's list).
-- GK.Gui.Click answers once it has seen the click's effect (a page opened, a value of the page's
-- data changed); a page's list is read once the layout has arranged it.
local function toggle_ai(game)
    local opened = game.ui([[ return GK.Gui.Click("MainMenu", "ModsButton") ]])
    check(opened.ok, "the mod manager opened", opened)
    local page = game.ui([[ return GK.Gui.Click("ModBrowser", "installedButton") ]])
    check(page.ok, "its Installed page opened", page)
    need(game.wait("ui", "GK.Gui.Look('ModBrowserInstalled', { layout = true }).laidOut", 10), "the Installed page is laid out")
    local names = game.ui([[
        local l = GK.Gui.Look("ModBrowserInstalled", { items = 20 })
        local out = {}
        for _, x in ipairs(l.controls or {}) do if x.items then for _, i in ipairs(x.items) do out[#out + 1] = tostring(i.text) end end end
        return out
    ]])
    local place
    for i, n in ipairs(names or {}) do if n == "GateKit AI" then place = i end end
    need(place, "GateKit AI is on the Installed page", names)
    local toggled = game.ui([[ return GK.Gui.Click("ModBrowserInstalled", "modEnableCheckBox", ]] .. place .. [[) ]])
    check(toggled.ok, "GateKit AI's check box took the click", toggled)
    local closed = game.ui([[ return GK.Gui.Click("ModBrowserInstalled", "closeButtonTop") ]])
    check(closed.ok, "the mod manager closed", closed)
end

-- The message box's button bound to UIAccept ("yes"), clicked: the box's buttons as GK.Gui.Look
-- gives them, in the order of the box's own Actions (GK.Gui.Blocking's summary names each one's
-- bound event, whatever the language).
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

-- Continue: the quick save is loaded; the game's checks of the save's mods are answered "go on".
local function continue(game)
    local since = game.mark()
    check(game.ui([[ return GK.Gui.Click("MainMenu", "ContinueButton").ok ]]), "Continue ran")
    -- The game's own checks of a save whose mods differ, each answered through its own button as
    -- it comes, until the load ends: the page ModVerification (no blocking window of the registry:
    -- its `ui_page` row) - Start Game; the box that warns of the mods disabled - its UIAccept
    -- button. Their answer starts the load, whose reset of the client's Lua may drop the click's
    -- own answer: the next row (the load's end) is what tells that the click took.
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
    game.save("continue-" .. tostring(game.now()) .. ".txt", fmt({ answered = answered }) .. "\n")
    game.wait_state("Running", "client", 300)
end

function run(game)
    game.wait_state("Running", "client", 120)
    step("the start composed the mod's hint", "observation", function()
        local r = read(game, "start")
        check(r.has, KEY .. " is in the game's list after the start", r)
        eq(r.source, AI, KEY .. " is GateKit AI's")
        eq(r.listSource, "LoadModFromSettings", "the composition read the game's resolved module list")
    end)
    step("a quick save", "fair", function()
        local since = game.mark()
        check(game.ui("return GK.Gui.QuickSave()").ok, "the quick save ran")
        game.wait_attention({ "saving_ended" }, 60, since)
    end)
    step("the main menu", "fair", function() to_menu(game) end)
    local since = last_row(game)
    step("GateKit AI disabled in the game's mod manager, the save loaded", "fair", function()
        toggle_ai(game)
        continue(game)
    end)
    step("the mod's hint is gone", "observation", function()
        local r = read(game, "disabled")
        local seen = rows(game, since)
        game.save("disabled-rows.txt", fmt(seen) .. "\n")
        eq(r.aiLoaded, false, "GateKit AI is not loaded")
        eq(r.has, false, KEY .. " left the game's list")
        eq(r.moment, "LoadModule", "the list in force was composed at the reload")
        eq(r.pending, false, "the reload's composition is in the game's list")
        local reload, installed
        for _, x in ipairs(seen) do
            if x.what == "HintsComposed" and x.moment == "reload" then reload = x end
            if x.what == "HintsComposed" and x.moment == "installed" then installed = x end
        end
        check(reload and installed, "the reload's composition is a row, and its putting into the list another", seen)
        eq(reload.gameCount, installed.gameCount, "the game's own file gave the same entries again")
    end)
    step("the main menu again", "fair", function() to_menu(game) end)
    since = last_row(game)
    step("GateKit AI enabled again, the save loaded", "fair", function()
        toggle_ai(game)
        continue(game)
    end)
    step("the mod's hint is back", "observation", function()
        local r = read(game, "enabled")
        game.save("enabled-rows.txt", fmt(rows(game, since)) .. "\n")
        eq(r.aiLoaded, true, "GateKit AI is loaded again")
        check(r.has, KEY .. " is back in the game's list", r)
        eq(r.source, AI, KEY .. " is GateKit AI's")
        eq(r.moment, "LoadModule", "the list in force was composed at the reload")
    end)
end
