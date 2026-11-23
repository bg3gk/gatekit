-- A mod shows another scene in the main menu by the game's own data: a module's meta.lsx names the
-- menu's level (`MenuLevelName`), and code:ls::ModManager::GetMenuLevel takes the last loaded
-- module that names one. Here both bridge mods name one through a loose meta.lsx of their own
-- folder (ours, `files`): Weave, loaded first, the prologue's TUT_Avernus_C, and the bridge,
-- loaded after it, the lobby's SYS_Menuscreen_Camp_A.
-- The rule: the menu's level is the bridge's; the "press any key" page and the main menu come and
-- work on it, and a save loads from that menu. The camera is what the level gives: Camp_A has no
-- cinematic arena trigger, so no menu timeline plays and the active camera is the spectator
-- camera the game places by the level's origin (no scene camera of a timeline). The view a level
-- shows is set by its triggers' timelines (tests/startup/menu/camera).
-- Class: the key and the load are fair (game.key, GK.Gui.LoadLatest); the rest observation.
-- Run: gatekit --instance <copy> test tests/startup/menu/scene

local M = require("_menu")

test { until_lua = true,
       files = {
           ["Data/Mods/Gwynerva_Weave/meta.lsx"] = "weave_meta.lsx",
           ["Data/Mods/Gwynerva_GateKit/meta.lsx"] = "gatekit_meta.lsx",
       },
       about = "The last loaded module's MenuLevelName gives the menu's level; the splash page, the main menu and a save's load work on it." }

local LEVEL = "SYS_Menuscreen_Camp_A"   -- the bridge's (gatekit_meta.lsx); Weave's names TUT_Avernus_C

function run(game)
    local start = game.mark()
    step("the menu's timelines and camera are recorded from the first Lua state", "observation", function() game.client(M.ARM) end)
    step("the press-any-key page is up", "observation", function()
        game.wait_attention({ "splash" }, 120, start)
        need(game.wait("client", M.SPLASH_READY, 30), "the page's fade-in is over")
    end)
    step("the menu's level is the last module's", "observation", function()
        local level = game.client([[
            local root = Ext.Entity.GetAllEntitiesWithComponent("LevelRoot")[1]
            return root and tostring(root.LevelRoot.LevelName)
        ]])
        eq(level, LEVEL, "the menu's level is the bridge's MenuLevelName, not Weave's")
    end)
    step("the camera is the level's spectator camera, no menu timeline plays", "observation", function()
        local r = game.client(M.READ)
        game.shot("splash.jpg")
        game.save("splash.txt", fmt({ cams = M.cameras(r), error = r.error }) .. "\n" .. fmt(r.rows) .. "\n")
        check(not r.error, "the recorder ran", r.error)
        for _, row in ipairs(r.rows) do
            check(not row.timeline, "no timeline is loaded on this menu level", row)
        end
        eq(M.cameras(r), { "none" }, "the one active camera is no timeline's scene camera")
    end)
    step("the page's key", "fair", function() game.key("space") end)
    step("the main menu comes", "observation", function()
        check(game.wait("ui", "(GK.Gui.Stuck()) == 'mainmenu'", 30), "the main menu is up after the key")
        game.shot("menu.jpg")
    end)
    step("a save loads from this menu", "fair", function()
        local since = game.mark()
        local first = game.ui("return GK.Gui.LoadLatest()")
        check(first.ok ~= false, "the load window opens", first)
        -- the window's `load` row comes before its list of saves is filled (a load asked at the
        -- row answered "no saves"): the list in the window's data is the state waited for
        game.wait_attention({ "load" }, 10, since)
        need(game.wait("ui", M.SAVES_LISTED, 10), "the load window lists the saves")
        local second = game.ui("return GK.Gui.LoadLatest()")
        check(second.ok ~= false, "the newest save is loaded", second)
    end)
    step("the session runs", "observation", function()
        check(game.wait_state("Running", "client", 180), "the client reaches Running after the save's load")
    end)
end
