-- The player's own avatar stands in the main menu's scene, by the game's own data, with no session.
-- What the menu knows of the latest save is the load window's data (gui::DCSavegames): the
-- playthrough the window selects when it opens (SelectedPlaythrough) is the one of the newest save
-- (its LatestSave), and of the avatar it holds only the name (ProtagonistName); the save's look
-- (CharacterCreationAppearance) is in the save's world, which only a session's load reads. An avatar
-- that is an origin is named by the origin's DisplayName (resource::Origin), and the origin's
-- GlobalTemplate is its figure; with no save the figure is an origin companion picked at random
-- among those the game offers in character creation (AvailableInCharacterCreation, with a
-- GlobalTemplate of their own).
-- The figure is a timeline dummy made on the client (Ext.ClientVisual.CreateDummy, the game's
-- code:ecl::TLPreviewDummyHelpers::CreateDummy and its siblings), standing in its animation
-- blueprint's idle before the menu's camera at rest, lit by one of the level's torch lights put
-- before it (a light with LightMovement follows its Transform).
-- The rule: the newest save's playthrough is the one the window selects; its avatar's origin, read
-- by name, gives the figure; the figure stands in the menu's level from that origin's template and
-- race and is animated; with no save an origin companion stands there instead. A custom avatar's
-- look is not shown: no game function reads it without the session (the step says so and the
-- figure is the random companion).
-- Class: the load window's button and its Escape are fair; the figure and the light are cheats
-- (the scene's own); the rest observation.
-- Run: gatekit --instance <copy> test tests/startup/menu/avatar -k avatar/latest

local M = require("_menu")

-- reset = "game": main-menu tests share one game as it is left, and a test before this one may leave a
-- session (loads/new_game starts a game); this test needs the main menu itself, so the game is started anew.
test { menu = true, reset = "game",
       about = "The newest save's avatar (an origin, by its name), or with no save a random origin companion, stands in the main menu's scene as a client figure from the origin's template, animated and lit." }

local LIGHT = "213b0683-f7d3-4d40-8259-f6c0e046e503"   -- a torch's point light of SYS_Menuscreen_Act3_B, with LightMovement

-- The scene's functions in the client's Lua: GKAvatar.Origins(), .Choose(name), .Stand(template), .Read(), .Clear().
local DEFINE = [[
    GKAvatar = GKAvatar or {}
    local A = GKAvatar
    local function try(f) local ok, v = pcall(f) if ok then return v end end

    -- The origins the game offers in character creation, each with its figure's template.
    function A.Origins()
        local list = {}
        for _, id in ipairs(Ext.StaticData.GetAll("Origin")) do
            local o = Ext.StaticData.Get(id, "Origin")
            if o.AvailableInCharacterCreation == 1 then
                local handle = try(function() return o.DisplayName.Handle.Handle end)
                list[#list + 1] = { origin = tostring(id), name = o.Name, race = tostring(o.RaceUUID),
                    template = tostring(o.GlobalTemplate),
                    shown = handle and Ext.Loca.GetTranslatedString(handle) or "" }
            end
        end
        return list
    end

    local NULL = "00000000-0000-0000-0000-000000000000"
    -- The avatar's origin by the name the save gives; else a random origin companion.
    function A.Choose(protagonist)
        local companions = {}
        for _, o in ipairs(A.Origins()) do
            if o.template ~= NULL then
                if protagonist and o.shown == protagonist then return { why = "the save's avatar", origin = o } end
                companions[#companions + 1] = o
            end
        end
        local pick = companions[Ext.Math.Random(1, #companions)]
        return { why = protagonist and "a custom avatar: its look is not readable without the session" or "no save",
                 origin = pick, companions = #companions }
    end

    local function menuCamera()
        for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Camera")) do
            if try(function() return e.SoundListener end) then return e end
        end
    end
    local function quatY(degrees)
        local half = math.rad(degrees) / 2
        return { 0, math.sin(half), 0, math.cos(half) }
    end

    -- The figure `ahead` metres along the camera's look on the ground, `right` to the picture's
    -- right, on the floor `down` metres under the camera; turned to the camera; the light between.
    function A.Stand(template, ahead, right, down)
        A.Clear()
        local cam = menuCamera()
        if not cam then return { ok = false, error = "no menu camera" } end
        local view = cam.Camera.Controller.Camera.ViewMatrix
        local p = cam.Transform.Transform.Translate
        local fx, fz = view[3], view[11]
        local l = math.sqrt(fx * fx + fz * fz)
        fx, fz = fx / l, fz / l
        local spot = { p[1] + fx * ahead + view[1] * right, p[2] - down, p[3] + fz * ahead + view[9] * right }
        -- turned to the camera: an origin's figure looks along its -z when not turned
        local yaw = math.deg(math.atan(p[1] - spot[1], p[3] - spot[3])) + 180
        A.entity = Ext.ClientVisual.CreateDummy(template, spot, quatY(yaw))
        if not A.entity then return { ok = false, error = "no figure from " .. template } end
        A.spot, A.template = spot, template
        for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Light")) do
            if tostring(e.Light.UUID) == "]] .. LIGHT .. [[" and try(function() return e.LightMovement end) then
                local home = e.Transform.Transform.Translate
                A.light, A.lightHome = e, { home[1], home[2], home[3] }
                local dx, dz = p[1] - spot[1], p[3] - spot[3]
                local reach = math.sqrt(dx * dx + dz * dz)
                local t = e.Transform.Transform
                t.Translate = { spot[1] + dx / reach * 1.6, spot[2] + 1.6, spot[3] + dz / reach * 1.6 }
                e.Transform.Transform = t
            end
        end
        return { ok = true, spot = spot, yaw = yaw, lit = A.light ~= nil }
    end

    -- What the figure is, read back from its entity.
    function A.Read()
        local e = A.entity
        if not e then return nil end
        local visual = try(function() return e.Visual.Visual end)
        local d = try(function() return e.TLPreviewDummy end)
        local root = d and Ext.Template.GetRootTemplate(tostring(d.OriginalCharacterTemplate))
        -- the template's race and the races above it (a subrace's ParentGuid is its race)
        local races, id = {}, root and tostring(root.Race)
        while id and id ~= NULL and #races < 4 do
            races[#races + 1] = id
            local race = Ext.StaticData.Get(id, "Race")
            id = race and tostring(race.ParentGuid)
        end
        return { loaded = visual ~= nil and try(function() return e.SceneAttach end) ~= nil,
                 template = d and tostring(d.OriginalCharacterTemplate),
                 races = races,
                 level = try(function() return e.Level.LevelName end),
                 animated = try(function() return e.AnimationBlueprint end) ~= nil and try(function() return e.AnimationUpdate end) ~= nil,
                 at = try(function() return e.Transform.Transform.Translate end) }
    end

    function A.Clear()
        if A.light and A.lightHome then
            pcall(function() local t = A.light.Transform.Transform; t.Translate = A.lightHome; A.light.Transform.Transform = t end)
        end
        if A.entity then pcall(Ext.ClientVisual.DestroyDummy, A.entity) end
        A.entity, A.light, A.lightHome = nil, nil, nil
        return true
    end
    return true
]]

-- The newest save as the window's data gives it, and the newest TimeString over every save it lists.
local LATEST = [[
    local d = GK.Gui.Data("LoadGame")
    local p = d.SelectedPlaythrough
    local s = p and p.LatestSave
    local function key(t)   -- "dd/mm/yyyy hh:mm" sorted as text by year, month, day, time
        local dd, mm, yy, hm = string.match(t or "", "(%d+)/(%d+)/(%d+) (%d+:%d+)")
        return dd and (yy .. mm .. dd .. hm) or ""
    end
    local newest = ""
    for i = 1, #d.ExistingSaves do
        local k = key(d.ExistingSaves[i].TimeString)
        if k > newest then newest = k end
    end
    return { protagonist = p and p.ProtagonistName, title = s and s.Title, time = s and s.TimeString,
             newest = key(s and s.TimeString) == newest, saves = #d.ExistingSaves }
]]

local function hasRace(r, race)
    for _, id in ipairs(r.races or {}) do if id == race then return true end end
    return false
end

local function stand(game, template, name)
    local placed = game.client(string.format("return GKAvatar.Stand(%q, 5, 1.2, 0.88)", template))
    check(placed.ok, "the figure is made", placed)
    check(placed.lit, "a torch light of the level stands before it", placed)
    need(game.wait("client", "(GKAvatar.Read() or {}).loaded", 20), "the figure's visual is loaded and in the scene")
    local r = game.client("return GKAvatar.Read()")
    game.save(name .. ".txt", fmt({ placed = placed, read = r }) .. "\n")
    game.shot(name .. ".jpg")
    return r
end

function run(game)
    step("the main menu stands, its camera at rest", "observation", function()
        game.wait("ui", "GK.Gui.Stuck() == 'mainmenu'", 60)
        need(game.wait("client", [[(function()
            for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("TimelineData")) do
                if e.ClientTimelineLoaded and tostring(e.ClientTimelineLoaded.Timeline) == "]] .. M.MM_ACCEPT .. [[" then
                    return e.TimelineData.PlaybackState ~= 2
                end
            end
            return true
        end)()]], 30), "the descent is over")
        game.client(DEFINE)
    end)
    local latest
    step("the load window's button", "fair", function()
        local r = game.ui("return GK.Gui.Click('MainMenu', 'LoadGameButton')")
        check(r.ok, "the load window opens", r)
        need(game.wait("ui", M.SAVES_LISTED, 10), "the load window lists the saves")
    end)
    step("the newest save and its avatar, from the window's data", "observation", function()
        latest = game.ui(LATEST)
        game.save("latest.txt", fmt(latest) .. "\n")
        check(latest.newest, "the playthrough the window selects holds the newest save", latest)
        check(latest.protagonist and latest.protagonist ~= "", "the save names its avatar", latest)
    end)
    step("Escape closes the window", "fair", function()
        game.key("escape")
        need(game.wait("ui", "GK.Gui.Stuck() == 'mainmenu'", 10), "the main menu is back")
    end)
    local chosen
    step("the avatar's origin, by its name", "observation", function()
        chosen = game.client(string.format("return GKAvatar.Choose(%q)", latest.protagonist))
        game.save("chosen.txt", fmt(chosen) .. "\n")
        check(chosen.why == "the save's avatar", "the save's avatar is an origin the game lists", chosen)
    end)
    step("the avatar stands in the menu's scene", "cheat", function()
        local r = stand(game, chosen.origin.template, "avatar")
        eq(r.template, chosen.origin.template, "the figure is the origin's template")
        check(hasRace(r, chosen.origin.race), "its template's race is the origin's (or its subrace)", r.races, chosen.origin.race)
        check(r.animated, "it is animated by its blueprint (its idle)", r)
        check(r.level and r.level ~= "", "it stands in the menu's level", r)
        game.client("return GKAvatar.Clear()")
    end)
    step("with no save: a random origin companion", "cheat", function()
        local pick = game.client("return GKAvatar.Choose(nil)")
        game.save("no_save.txt", fmt(pick) .. "\n")
        eq(pick.why, "no save", "the choice says why")
        check(pick.companions >= 2, "the game offers several companions", pick)
        local r = stand(game, pick.origin.template, "companion")
        eq(r.template, pick.origin.template, "the figure is the companion's template")
        check(hasRace(r, pick.origin.race), "its template's race is the companion's (or its subrace)", r.races, pick.origin.race)
        check(r.animated, "it is animated by its blueprint (its idle)", r)
        game.client("return GKAvatar.Clear()")
        eq(game.ui("return GK.Gui.Stuck()"), "mainmenu", "the main menu stands")
    end)
end
