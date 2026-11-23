-- The server's part of the front and of the stand (mods/AI/README.md, "One door", "Scenes"):
--
--   AI.Forwarded(msg)           a tool call the client passed on: answered over the channel
--   AI.StandCall(name, which, code)   a scene of the stand's manifest armed ("arm") or disarmed
--                               ("off") on its side; an observation of { ok, did, error }
--   after a load                every scene the session armed on this level is armed again from
--                               the code the mod kept
--   the manifest's checkpoint   the game's autosave when the named flag of the scene turns true
--   the manifest's reset        when the named flag turns true, the client loads the newest save
--                               from the game-over screen after `reset_hold` seconds
--
-- The server says hello to the client every second; the client answers, and passes tool calls on
-- only while it hears it.

local GK = AI.GK
local U = AI.Util
local St = AI.Stand

local function level() return U.try(GK.Stage.CurrentLevel) end

-- ---------------------------------------------------------------------------------------
-- Calls the client passed on

local forwards = {}

function AI.Forwarded(msg)
    local ok, value = pcall(AI.Tool, msg.tool, type(msg.args) == "table" and msg.args or {})
    if not ok then value = AI.Encode({ ok = false, mode = AI.Mode.name, did = "", why_not = "the bridge failed: " .. tostring(value), hint = "tell the owner", now = {} },
        { "ok", "mode", "did", "why_not", "hint", "now" }) end
    forwards[#forwards + 1] = { id = msg.forward, step = GK.Follow(value) }
end

local function answerForwards()
    local i = 1
    while i <= #forwards do
        local f = forwards[i]
        local ok, text = pcall(f.step)
        if not ok then text = tostring(text) end
        if text ~= nil then
            if type(text) == "table" then text = Ext.Json.Stringify(text) end
            pcall(function() AI.Channel:Broadcast({ reply = f.id, text = tostring(text) }) end)
            table.remove(forwards, i)
        else
            i = i + 1
        end
    end
end

-- ---------------------------------------------------------------------------------------
-- Arming

--- Runs the scene's "arm" or "off" code on its side; `code` is kept for the next time. Answers an
--- observation that ends in { ok, did, error }. The session marks the scene armed on this level
--- (or not armed) when it ran.
function AI.StandCall(name, which, code, again)
    local e = St.Entry(name)
    local chunk = which == "arm" and St.Chunk(code, e.options) or code
    local got
    if e.side == "server" then
        got = St.Run(chunk)
    else
        AI.Ask({ act = "run", code = chunk, ui = e.side == "ui" }, function(a) got = a end)
    end
    local verb = which == "arm" and (again and "armed again" or "armed") or "disarmed"
    return GK.Observe({ ok = true, waiting = "the scene" }, function(_, final)
        if got == nil then
            if final then return { ok = false, error = "the client did not run the scene's code" } end
            return nil
        end
        if got.ok == false then
            AI.Row("ai_scene", { name = name, call = which, ok = false, error = tostring(got.error) })
            return { ok = false, error = name .. " (" .. e.side .. "): " .. U.cut(tostring(got.error or "failed"), 200) }
        end
        St.Mark(name, which == "arm" and (level() or "") or false)
        AI.Row("ai_scene", { name = name, call = again and "rearm" or which, ok = true })
        return { ok = true, did = name .. " " .. verb .. " (" .. e.side .. ")" }
    end, 5000)
end

-- ---------------------------------------------------------------------------------------
-- After a load, the checkpoint and the reset

-- this Lua state's: checkpoints made, the reset asked for. A Lua state that starts in a running
-- level is a reset of Lua alone (`gatekit reload`): the world kept the scenes' figures, so
-- nothing is armed again; a load starts Lua before the level runs.
local W = { made = {}, rearmed = tostring(Ext.Utils.GetGameState()) == "Running" }

local function rearm()
    local here = level()
    if not here then return end
    for name, a in pairs(St.Armed()) do
        local code = St.Code(name, "arm")
        if a.level == here and code and St.Entry(name) then
            local follow = GK.Follow(AI.StandCall(name, "arm", code, true))
            W.following = W.following or {}
            W.following[#W.following + 1] = follow
        end
    end
end

local function checkpoint(name, flag)
    GK.AutoSave(true)
    AI.Ask({ act = "autosave", on = true }, function() end)
    local ok, problem = pcall(Osi.AutoSave)
    W.saveOff = GK.Ticks() + 300
    W.made[name] = true
    AI.Checkpoint = { scene = name, flag = flag, ok = ok, level = level() }
    AI.Row("ai_scene", { name = name, call = "checkpoint", ok = ok, error = not ok and tostring(problem) or nil })
end

-- `running`: the game runs (a checkpoint is made only then); the reset is watched in any state,
-- since the game-over screen pauses the game.
local function watch(running)
    local scenes = U.try(AI.SceneTable)
    if type(scenes) ~= "table" then return end
    local here = level()
    for name, a in pairs(St.Armed()) do
        local e, scene = St.Entry(name), scenes[name]
        if e and a.level == here and type(scene) == "table" then
            if running and type(e.checkpoint) == "string" and scene[e.checkpoint] and not W.made[name] then
                checkpoint(name, e.checkpoint)
            end
            if type(e.reset) == "string" and scene[e.reset] and not W.resetting then
                W.resetting = true
                AI.Ask({ act = "reset", hold = tonumber(e.reset_hold) or 4 }, function() end)
                AI.Row("ai_scene", { name = name, call = "reset", ok = true })
            end
        end
    end
end

local tick = 0
Ext.Events.Tick:Subscribe(function()
    tick = tick + 1
    if #forwards > 0 then answerForwards() end
    if W.following then
        for i = #W.following, 1, -1 do
            if W.following[i]() ~= nil then table.remove(W.following, i) end
        end
        if #W.following == 0 then W.following = nil end
    end
    if tick % 30 ~= 0 then return end
    pcall(function() AI.Channel:Broadcast({ hello = true }) end)
    local running = tostring(Ext.Utils.GetGameState()) == "Running"
    -- armed again once the client has answered a hello in this Lua state, a second after Running
    if running and not W.rearmed and AI.Client.alive then
        W.rearmed = true
        pcall(rearm)
    end
    if W.saveOff and GK.Ticks() >= W.saveOff then
        W.saveOff = nil
        GK.AutoSave(false)
        AI.Ask({ act = "autosave", on = false }, function() end)
    end
    pcall(watch, running)
end)
