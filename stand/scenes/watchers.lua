-- Scene "watchers" (the video stand): Raphael and Mizora stand somewhere on the level, idle, turned
-- towards the party. When a party member first sees one, it is marked "seen" for the feed; when a
-- party member comes within 6 m, it says a short line and vanishes in a puff, as Raphael does.
-- Arm:    ./gatekit --instance <copy> exec server "$(cat stand/scenes/watchers.lua)"
--         (other places: set GKSceneOptions = { watchers = { raphael = {x,y,z}, mizora = {x,y,z} } } first)
-- Disarm: ./gatekit --instance <copy> exec server "$(cat stand/scenes/watchers_off.lua)"
-- State:  GKScene.watchers = { armed, fired (any vanished), log, who = { raphael = { id, at, seen,
--         seenBy, distance, vanished }, mizora = {...} }, notice = text for the feed or nil }

GKScene = GKScene or {}
local NAME = "watchers"
local POOF = "f0cf792a-0f74-d17e-ad0d-6052a6131416"      -- VFX_Script_Stub_Poof_01: Raphael's own vanish
local SEE_M, NEAR_M = 25, 6
local options = (GKSceneOptions and GKSceneOptions.watchers) or {}
local CAST = {
    raphael = {
        name = "Рафаил",
        -- his root makes a bare human; his look is on the placed object S_GLO_Monitor: a transform to it
        template = "acd7060a-43bd-4dc9-b050-9d9b2cf7b7df", look = "f65becd6-5cd7-4c88-b85e-6dd06b60f7b8",
        at = options.raphael or { 0.8, 23.5, -429.2 },       -- Myrnath's platform, above the surgery room
        line = "Восхитительно. До скорой встречи.",
        style = { color = "#FF4040", bold = true, italic = true, prefix = "♦ ", suffix = " ♦", accent = "#FFD040" },
    },
    mizora = {
        name = "Мизора",
        template = "2fbd583e-0507-41c7-be06-15b1f77797d8",
        at = options.mizora or { 0.7, 18.2, -389.0 },        -- the helm's antechamber, by the restoration pod
        line = "Какой забавный червячок.",
        style = { color = "#FF60C0", bold = true, italic = true, prefix = "• ", suffix = " •", accent = "#FFC0E0" },
    },
}

local old = GKScene[NAME]
if old and old.armed then return { ok = true, already = true, who = old.who } end

local S = { armed = true, fired = false, log = {}, who = {}, notice = nil }
GKScene[NAME] = S

local function log(text)
    S.log[#S.log + 1] = { tick = GK.Speed.Clock().ticks, ms = Ext.Utils.MonotonicTime(), text = text }
end
local function try(what, fn, ...)
    local ok, v = pcall(fn, ...)
    if not ok then log("failed: " .. what .. ": " .. tostring(v)) end
    return ok and v or nil
end
local function later(ms, fn)
    Ext.Timer.WaitForRealtime(ms, function() if S.armed then try("a step", fn) end end)
end

-- a stage scope for the spawns' ledger: the stand's when one is open, else the scene's own
if not (GK.Stage.Ledger and GK.Stage.Ledger.baseline) then
    local b = GK.Stage.Begin()
    if b and b.lost then GK.Stage.Begin{ fresh = true } end
    S.ownScope = true
end

for key, c in pairs(CAST) do
    GK.Say.Styles["watcher_" .. key] = c.style
    local made = try("spawn " .. key, GK.Stage.Spawn, { template = c.template, at = c.at, faction = "neutral" })
    local id = made and made[1]
    S.who[key] = { id = id, name = c.name, at = c.at, seen = false, vanished = false }
    if id then
        if c.look then later(300, function() GK.Stage.Transform(c.look, id) end) end
        log(c.name .. " placed at " .. table.concat(c.at, ", "))
    end
end

local function vanish(key, member)
    local w, c = S.who[key], CAST[key]
    w.vanishing = true
    S.fired = true
    log(c.name .. ": " .. member .. " came within " .. NEAR_M .. " m")
    later(600, function() try("focus", GK.Camera.Focus, w.id, { glide = true, hold = 4.6 }) end)
    later(1100, function()
        try("line", GK.Say.Line, w.id, c.line, { style = "watcher_" .. key })
        log(c.name .. ": " .. c.line)
    end)
    later(3900, function()
        local x, y, z = Osi.GetPosition(w.id)
        try("poof", Osi.PlayEffectAtPosition, POOF, x, y, z, 1.0)
        try("poof", Osi.PlayEffect, w.id, POOF, "", 1)
    end)
    later(4200, function()
        GK.Stage.Remove(w.id)
        w.vanished = true
        log(c.name .. " vanished")
    end)
end

local function look()
    if not S.armed then return end
    local notice = nil
    for key, w in pairs(S.who) do
        if w.id and not w.vanished and not w.vanishing then
            local nearest, nd = nil, 1e9
            for _, m in ipairs(GK.Stage.Party()) do
                local d = Osi.GetDistanceTo(m, w.id)
                if d and d < nd then nearest, nd = m, d end
                if d and d < SEE_M and not w.seen then
                    local sees = try("sight", GK.See.Perceives, m, w.id)
                    if sees then
                        w.seen, w.seenBy, w.seenAt = true, m, Ext.Utils.MonotonicTime()
                        log(CAST[key].name .. " seen by " .. m .. string.format(" at %.1f m", d))
                    end
                end
            end
            w.distance = nd < 1e9 and math.floor(nd * 10) / 10 or nil
            local t = Ext.Utils.MonotonicTime()
            if nearest and nd < SEE_M and t - (w.steered or 0) > 2000 then
                w.steered = t
                try("turn", Osi.SteerTo, w.id, nearest, 0)
            end
            if w.seen then notice = (notice and notice .. "; " or "") .. "someone is watching from afar (" .. CAST[key].name .. ")" end
            if nearest and nd < NEAR_M then vanish(key, nearest) end
        end
    end
    S.notice = notice
    Ext.Timer.WaitForRealtime(300, look)
end
later(600, look)

log("armed")
local out = { ok = true, armed = true }
for key, w in pairs(S.who) do out[key] = w.id or "not placed" end
return out
