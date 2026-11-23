-- A mod gives the camera after the "press any key" page its own move, of another length, by the
-- game's own data: the menu level's cinematic arena trigger that binds timelines to the menu's
-- accept event (MainMenuStartEventType 2, camera event 4) is the game's MM_Accept_ArenaTrigger;
-- a mod's trigger file on that level with the same MapKey (accept.lsx, ours, written as LSF at the
-- start: `files`) replaces it, and lists MainMenu/MM_New (2 s, its own scene camera) instead of
-- MM_Accept (9.54 s) and MM_Accept_Temple. A timeline's scene camera stands relative to the
-- trigger that plays it (MM_New's camera is where this trigger puts it, not where the game's
-- MM_New_ArenaTrigger would).
-- The rule: the page's key plays MM_New and never MM_Accept, its camera becomes the active one in
-- the same tick, it lasts MM_New's 2 s, attention tells the descent by the timeline the accept
-- event started (`menu_descent` / `menu_descent_ended` with MM_New), and the main menu is up
-- while it plays and after. Without the file the descent is MM_Accept's 9.5 s (tests/startup/loads/start).
-- Class: the key is fair (game.key); the rest observation.
-- Run: gatekit --instance <copy> test tests/startup/menu/camera

local M = require("_menu")

test { until_lua = true,
       files = {
           ["Data/Mods/Gwynerva_GateKit/Levels/SYS_Menuscreen_Act3_B/Triggers/GK_Accept_ArenaTrigger.lsf"] = "accept.lsx",
       },
       about = "A mod's arena trigger of the accept event replaces the game's: the page's key plays its timeline and camera for that timeline's length, attention tells it, and the menu works during and after." }

local NEW_CAMERA = "26db86fd-17b5-449a-a602-d353b93e8a50"   -- MM_New's scene camera
local TRIGGER = { 985.715, -1.708, -1072.094 }             -- accept.lsx's Transform.Position
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
    step("attention tells the mod's descent and the menu is up during it", "observation", function()
        went = game.wait_attention(KINDS, 30, before)
        eq(went.what, "menu_descent", "the descent begins on the key")
        eq(went.timeline, M.MM_NEW, "the descent is the timeline the accept event started: the mod's MM_New")
        eq(game.ui("return GK.Gui.Stuck()"), "mainmenu", "the main menu is up while the camera moves")
    end)
    step("the descent ends with its timeline and the menu stays", "observation", function()
        local rest = game.wait_attention(KINDS, 30, (string.gsub(before, "client:(%d+)%.%d+", "client:%1." .. tostring(went.seq))))
        eq(rest.what, "menu_descent_ended", "the descent ends")
        eq(rest.timeline, M.MM_NEW, "the end is MM_New's")
        eq(rest.playback, 0, "MM_New is back at rest (0)")
        eq(game.ui("return GK.Gui.Stuck()"), "mainmenu", "the main menu is up after the camera's move")
        game.shot("menu.jpg")
    end)
    step("the record: MM_New, its camera, its length", "observation", function()
        local r = game.client(M.READ)
        game.save("record.txt", fmt({ accept = M.span(r, M.MM_ACCEPT), new = M.span(r, M.MM_NEW), open = M.span(r, M.MM_OPEN), cams = M.cameras(r), error = r.error }) .. "\n" .. fmt(r.rows) .. "\n")
        check(not r.error, "the recorder ran", r.error)
        check(not M.span(r, M.MM_ACCEPT).started, "MM_Accept never plays")
        local new = M.span(r, M.MM_NEW)
        check(new.started and new.ended, "MM_New played and ended", new)
        check(math.abs(new.ended - new.started - 2000) <= 250, "MM_New lasts its own 2 s, not MM_Accept's 9.5 s", new.ended - new.started)
        local cam
        for _, row in ipairs(r.rows) do
            if row.camera == NEW_CAMERA and not cam then cam = row end
        end
        check(cam, "MM_New's scene camera becomes the active one", M.cameras(r))
        check(math.abs(cam.t - new.started) <= 50, "in the tick MM_New starts", cam.t, new.started)
        local d = math.sqrt((cam.x - TRIGGER[1]) ^ 2 + (cam.y - TRIGGER[2]) ^ 2 + (cam.z - TRIGGER[3]) ^ 2)
        check(d < 5, "the camera stands by the mod's trigger (MM_New's own trigger is 140 m below)", cam, d)
    end)
end
