-- What the tests under tests/startup/menu share: a per-tick record of the main menu's timelines and
-- of the active camera, kept in the client's Lua from the "press any key" page on.

local M = {}

M.MM_ACCEPT = "d0fbd7c8-89e5-b1cf-5528-55fb12deb971"   -- MainMenu/MM_Accept, the game's descent
M.MM_OPEN = "02c1cb82-0d8e-9233-6ac5-79feaf9a43ff"     -- MainMenu/MM_Open
M.MM_NEW = "7d3dccdd-2376-0c04-337a-8cb46ee5fa63"      -- MainMenu/MM_New

--- Arms the record: every tick, each timeline's PlaybackState change and the active camera's
--- entity and place (each 100 ms), with Ext.Utils.MonotonicTime.
M.ARM = [[
    local R = { rows = {}, last = {}, cam = nil, camAt = -1e18, t0 = Ext.Utils.MonotonicTime() }
    _G.GK_MenuRecord = R
    if _G.GK_MenuTick then Ext.Events.Tick:Unsubscribe(_G.GK_MenuTick) end
    _G.GK_MenuTick = Ext.Events.Tick:Subscribe(function()
        local ok, err = pcall(function()
            local t = Ext.Utils.MonotonicTime()
            for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("TimelineData") or {}) do
                local id = e.ClientTimelineLoaded and tostring(e.ClientTimelineLoaded.Timeline) or tostring(e)
                local p = e.TimelineData.PlaybackState
                if R.last[id] ~= p then
                    R.rows[#R.rows + 1] = { t = t, timeline = id, playback = p }
                    R.last[id] = p
                end
            end
            for _, c in ipairs(Ext.Entity.GetAllEntitiesWithComponent("Camera") or {}) do
                if c.Camera.Active then
                    local actor = c.TimelineActorData and tostring(c.TimelineActorData.Actor) or "none"
                    if actor ~= R.cam or t - R.camAt >= 100 then
                        local p = c.Transform and c.Transform.Transform.Translate or {}
                        R.rows[#R.rows + 1] = { t = t, camera = actor, x = p[1], y = p[2], z = p[3] }
                        R.cam, R.camAt = actor, t
                    end
                end
            end
        end)
        if not ok then R.error = tostring(err) end
    end)
    return true
]]

--- The "press any key" page takes its key: the page is entered and the client's fade-in after it
--- is over (a key sent as soon as the page was up was dropped once and the page stayed). Read from the start's
--- timeline (Ext.StartupVideos.Timeline: the UIState and Fade rows), for game.wait("client", ...).
M.SPLASH_READY = [[(function()
    local entered = false
    for _, r in ipairs(Ext.StartupVideos.Timeline() or {}) do
        if r.What == "UIState" and r.Name == "SplashScreen" and r.Change == "Entered" then entered = true
        elseif entered and r.What == "Fade" and r.To == "None" then return true end
    end
    return false
end)()]]

--- The main menu's load window has its list of saves: the window's data (gui::DCSavegames,
--- ExistingSaves) is filled some frames after the window's `load` row, for game.wait("ui", ...).
M.SAVES_LISTED = [[(function()
    local d = GK.Gui.Data("LoadGame")
    local ok, n = pcall(function() return #d.ExistingSaves end)
    return ok and type(n) == "number" and n > 0
end)()]]

M.READ = [[
    local R = _G.GK_MenuRecord
    if not R then return nil end
    return { rows = R.rows, error = R.error, now = Ext.Utils.MonotonicTime() }
]]

--- The record's timeline rows of one timeline: { started = t of 0 -> 2, ended = t of 2 -> 0 }.
function M.span(record, timeline)
    local s = {}
    for _, r in ipairs(record.rows) do
        if r.timeline == timeline then
            if r.playback == 2 and not s.started then s.started = r.t end
            if r.playback ~= 2 and s.started and not s.ended then s.ended = r.t end
        end
    end
    return s
end

--- The cameras the record saw active, in order of first sight.
function M.cameras(record)
    local seen, list = {}, {}
    for _, r in ipairs(record.rows) do
        if r.camera and not seen[r.camera] then seen[r.camera] = true; list[#list + 1] = r.camera end
    end
    return list
end

return M
