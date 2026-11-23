-- The main menu does not wait for the camera, nor for the accept event's timelines to end: a
-- mod's trigger of the accept event (accept.lsx, the MapKey of the game's MM_Accept_ArenaTrigger,
-- ours, written as LSF at the start: `files`) lists only MainMenu/MM_Open_Boats - the menu's
-- ambient loop, which the open event already plays and which stays playing (PlaybackState 2) -
-- and no camera move: the open event's camera stays.
-- The rule: on the page's key the main menu comes at once and stays; attention opens `menu:descent` with MM_Open_Boats and, the timeline never coming to
-- rest (338 s long by its data, `gatekit data timeline MM_Open_Boats`), tells no end of it while
-- the menu stands: the end is told when the menu is left - here by a save's load, the player's
-- own way out of the menu - with the client's state entered (`to`), the timeline still playing.
-- No step waits for time: the descent's end is waited for as the game's answer to the menu's end.
-- Class: the key and the load are fair (game.key, GK.Gui.LoadLatest); the rest observation.
-- Run: gatekit --instance <copy> test tests/startup/menu/long

local M = require("_menu")

test { until_lua = true,
       files = {
           ["Data/Mods/Gwynerva_GateKit/Levels/SYS_Menuscreen_Act3_B/Triggers/GK_Accept_ArenaTrigger.lsf"] = "accept.lsx",
       },
       surprises = { "loading" },
       about = "The main menu comes and stays on the page's key while the accept event's looping timeline plays on; attention keeps the descent open until the menu is left." }

local BOATS = "20cb00dc-154f-4bbf-6611-5e8c62e6321b"
local KINDS = { "menu_descent", "menu_descent_ended" }

function run(game)
    local start = game.mark()
    step("the menu's timelines and camera are recorded from the first Lua state", "observation", function() game.client(M.ARM) end)
    step("the press-any-key page is up", "observation", function()
        game.wait_attention({ "splash" }, 120, start)
        need(game.wait("client", M.SPLASH_READY, 30), "the page's fade-in is over")
    end)
    local before = game.mark()
    step("the page's key", "fair", function() game.key("space") end)
    local went
    step("the descent opens with the looping timeline and the menu is up", "observation", function()
        went = game.wait_attention(KINDS, 30, before)
        eq(went.what, "menu_descent", "the descent begins on the key")
        eq(went.timeline, BOATS, "the descent is the mod's accept timeline, MM_Open_Boats")
        check(game.wait("ui", "(GK.Gui.Stuck()) == 'mainmenu'", 10), "the main menu is up")
    end)
    local r, boats
    step("the menu stands with the timeline playing and the descent open", "observation", function()
        local seen = game.events(before, KINDS)
        local ended
        for _, row in ipairs(seen.rows or {}) do
            if row.kind == "menu_descent_ended" then ended = row end
        end
        game.shot("menu.jpg")
        -- the record lives in this Lua state, which the load below resets: read now
        r = game.client(M.READ)
        boats = game.client([[
            for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("TimelineData")) do
                if tostring(e.ClientTimelineLoaded.Timeline) == "]] .. BOATS .. [[" then return Ext.Types.Serialize(e.TimelineData) end
            end
        ]])
        game.save("record.txt", fmt({ boats = M.span(r, BOATS), accept = M.span(r, M.MM_ACCEPT), cams = M.cameras(r), boatsData = boats, rows = seen.rows }) .. "\n" .. fmt(r.rows) .. "\n")
        check(not ended, "no end of the descent is told while the menu stands", ended)
        eq(boats and boats.PlaybackState, 2, "MM_Open_Boats is still playing")
        check(not M.span(r, BOATS).ended, "MM_Open_Boats never came to rest", M.span(r, BOATS))
        check(not M.span(r, M.MM_ACCEPT).started, "MM_Accept never plays")
        eq(game.ui("return GK.Gui.Stuck()"), "mainmenu", "the main menu is still up")
    end)
    local leave = game.mark()
    step("the menu left: the newest save loaded", "fair", function()
        local first = game.ui("return GK.Gui.LoadLatest()")
        check(first.ok ~= false, "the load window opens", first)
        game.wait_attention({ "load" }, 10, leave)
        need(game.wait("ui", M.SAVES_LISTED, 10), "the load window lists the saves")
        local second = game.ui("return GK.Gui.LoadLatest()")
        check(second.ok ~= false, "the newest save is loaded", second)
    end)
    step("the descent ends with the menu, its timeline still playing", "observation", function()
        local ended = game.wait_attention({ "menu_descent_ended" }, 60, leave)
        game.save("ended.txt", fmt(ended) .. "\n")
        local rows = game.events(before, { "menu_descent_ended" }).rows
        eq(#rows, 1, "one end of the descent is told since the key")
        eq(tostring(rows[1].payload.opened), tostring(went.seq), "the end closes the descent MM_Open_Boats opened")
        eq(rows[1].payload.playback, nil, "the end tells no rest of the timeline: it comes with the menu's end")
        check(ended.to and ended.to ~= "Menu", "the end names the client's state the menu was left for", ended)
    end)
end
