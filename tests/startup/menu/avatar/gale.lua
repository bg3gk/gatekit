-- Gale as a god in his epilogue's place, as the main menu's scene: the bridge's meta.lsx names
-- EPI_CIN_GaleHeaven_A as the menu's level (code:ls::ModManager::GetMenuLevel takes the last loaded
-- module's MenuLevelName, tests/startup/menu/scene). The level has no menu timeline: its two
-- Camera entities stand inactive at the origin and neither carries SoundListener.
-- The rule: the splash page and the main menu come on that level; god Gale (ORIGIN_Gale_God) is
-- made as a client figure on the temple's platform (BLD_GEN_Ramazith_Platform_A); one of the
-- level's cameras, made active and turned about him by writing its Transform every tick, draws
-- nothing: the frame stays black. A camera that draws is a timeline's scene camera (on
-- SYS_Menuscreen_Act3_B the same turn about the same figure is seen), and no timeline of the menu
-- plays on this level (the stopper in tests/startup/menu/scene: a mod's arena trigger makes none here).
-- Class: the key is fair; the figure and the camera are cheats; the rest observation.
-- Run: gatekit --instance <copy> test tests/startup/menu/avatar -k gale

local M = require("_menu")

test { until_lua = true,
       files = { ["Data/Mods/Gwynerva_GateKit/meta.lsx"] = "gale_meta.lsx" },
       about = "On Gale's epilogue level as the menu's level no camera draws: a level camera made active and turned about god Gale leaves the frame black." }

local GOD_GALE = "458fa72d-10e7-46bd-896b-0bb60e71bb29"   -- the root template ORIGIN_Gale_God

function run(game)
    local start = game.mark()
    step("the press-any-key page is up", "observation", function()
        game.wait_attention({ "splash" }, 120, start)
        need(game.wait("client", M.SPLASH_READY, 30), "the page's fade-in is over")
    end)
    step("the menu's level is Gale's", "observation", function()
        local level = game.client([[
            local root = Ext.Entity.GetAllEntitiesWithComponent("LevelRoot")[1]
            return root and tostring(root.LevelRoot.LevelName)
        ]])
        eq(level, "EPI_CIN_GaleHeaven_A", "the menu's level")
    end)
    step("the page's key", "fair", function() game.key("space") end)
    step("the main menu comes", "observation", function()
        check(game.wait("ui", "(GK.Gui.Stuck()) == 'mainmenu'", 30), "the main menu is up after the key")
    end)
    step("the level's cameras", "observation", function()
        local cams = game.client([[
            local r = {}
            for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Camera")) do
                r[#r + 1] = { active = e.Camera.Active, listener = e.SoundListener ~= nil,
                    at = e.Transform and e.Transform.Transform.Translate, turn = e.Transform and e.Transform.Transform.RotationQuat }
            end
            return r
        ]])
        game.save("cameras.txt", fmt(cams) .. "\n")
        game.shot("menu.jpg")
    end)
    step("god Gale on the temple's platform, the camera turning about him", "cheat", function()
        local r = game.client([[
            local G = { centre = { 28.03, 104.36, -240.29 }, radius = 4, up = 1.5, period = 20 }
            _G.GKGale = G
            G.entity = Ext.ClientVisual.CreateDummy("]] .. GOD_GALE .. [[", G.centre, { 0, 0, 0, 1 })
            local cam
            for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Camera")) do
                if e.Camera.Active then cam = e end
            end
            -- the level has no menu timeline, so none of its cameras is active: the first is made so
            if not cam then
                cam = Ext.Entity.GetAllEntitiesWithComponent("Camera")[1]
                if cam then cam.Camera.Active = true; G.activated = true end
            end
            G.camera = cam
            G.t0 = Ext.Utils.MonotonicTime()
            G.seen = {}
            G.sub = Ext.Events.Tick:Subscribe(function()
                local ok, err = pcall(function()
                    local a = 2 * math.pi * ((Ext.Utils.MonotonicTime() - G.t0) / 1000) / G.period
                    local c = G.centre
                    local p = { c[1] + G.radius * math.sin(a), c[2] + G.up, c[3] + G.radius * math.cos(a) }
                    local yaw = math.atan(c[1] - p[1], c[3] - p[3]) + math.pi   -- a camera looks along its -z
                    local t = G.camera.Transform.Transform
                    t.Translate = p
                    t.RotationQuat = { 0, math.sin(yaw / 2), 0, math.cos(yaw / 2) }
                    G.camera.Transform.Transform = t
                    G.last = { a = a, read = G.camera.Transform.Transform.Translate }
                end)
                if not ok then G.error = tostring(err) end
            end)
            return { made = G.entity ~= nil, camera = cam ~= nil }
        ]])
        check(r.made, "the figure is made", r)
        check(r.camera, "a camera of the level is taken", r)
        need(game.wait("client", "GKGale.entity.Visual ~= nil and GKGale.last ~= nil", 20), "the figure's visual is loaded and the camera turns")
    end)
    step("the camera follows its Transform and draws nothing", "observation", function()
        local s = game.client("return { last = GKGale.last, error = GKGale.error, activated = GKGale.activated }")
        game.save("orbit.txt", fmt(s) .. "\n")
        check(not s.error, "the turn ran", s.error)
        check(s.activated, "no camera of the level was active: one was made so", s)
        local d = math.sqrt((s.last.read[1] - 28.03) ^ 2 + (s.last.read[3] + 240.29) ^ 2)
        check(math.abs(d - 4) < 0.01, "the camera stands on the circle about Gale", s.last, d)
        game.shot("orbit.jpg")
        local px = game.pixel("orbit.jpg", 0.6, 0.5)
        check(px.r + px.g + px.b < 30, "the frame stays black", px)
        game.client("Ext.Events.Tick:Unsubscribe(GKGale.sub); Ext.ClientVisual.DestroyDummy(GKGale.entity); return true")
    end)
end
