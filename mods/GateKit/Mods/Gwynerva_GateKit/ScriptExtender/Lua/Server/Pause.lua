-- GK.Pause: stop the world while something outside it thinks.
--
--   GK.Pause(true)        the world stops
--   GK.Pause(false)       it goes on at whatever pace GK.Speed says
--   GK.Pause.State()      { paused, scale, engine, by }
--   GK.Pause.While(fn)    pause, run fn, put the pace back even if fn fails
--
-- The whole coverage loop leans on this: an agent decides between actions, and nothing may come
-- alive and vanish unseen while it does, or a finding stops being reproducible.
--
-- **What it actually is, said plainly.** Baldur's Gate 3 has no pause a mod can press. What it
-- has is the engine's own time scale (ls::GlobalSwitches::DeltaTimeScale, the field GK.Speed
-- already drives), which the server loop, the client loop and the UI multiply their frame delta
-- by, and which the engine itself clamps to 0.001..10. So a pause here is that scale at its
-- floor: one real second buys one millisecond of game time. Animations, timers, cooldowns and
-- the AI are frozen for every practical purpose and not mathematically stopped, the frame loop
-- still runs (so the bridge still answers and the window still draws), and sound is mixed in
-- real time and does not slow with it. A finding that turns on the difference between 0 and
-- 0.001 is not a finding this call can make; nothing else is affected.
--
-- It cooperates with GK.Speed rather than fighting it: while paused it stands as a rule of the
-- highest priority, so Speed's own poll cannot put the pace back under it. Unpausing removes the
-- rule and Speed's situation decides again.

GK.Pause = GK.Pause or {}
local Pause = GK.Pause

local FLOOR = 0.001   -- the engine's own lower clamp; below it the game refuses
local RULE = "gk-pause"

local state = Pause.Status or { paused = false, by = nil }
Pause.Status = state

local function switches()
    return Ext.Utils.GetGlobalSwitches()
end

-- The field under either name: GateKit's extender build calls it DeltaTimeScale, the stock one
-- AnimationBlueprintSpeedScale_M. The same float either way.
local function write(scale)
    local global = switches()
    local ok = pcall(function() global.DeltaTimeScale = scale end)
    if not ok then ok = pcall(function() global.AnimationBlueprintSpeedScale_M = scale end) end
    return ok
end

local function read()
    local global = switches()
    local ok, value = pcall(function() return global.DeltaTimeScale end)
    if not ok or value == nil then ok, value = pcall(function() return global.AnimationBlueprintSpeedScale_M end) end
    return ok and value or nil
end

--- Stops or restarts the world. `by` is a note for the record: who asked, and what for.
--- Returns the state afterwards.
function Pause.Set(paused, by)
    paused = paused and true or false
    if paused then
        state.paused, state.by = true, by
        -- A rule of the highest priority: GK.Speed's half-second poll would otherwise put the
        -- situation's own scale back a moment later.
        if GK.Speed and GK.Speed.Rule then
            GK.Speed.Rule(RULE, 1000000, function() return state.paused end, FLOOR)
        end
        write(FLOOR)
    else
        state.paused, state.by = false, nil
        if GK.Speed and GK.Speed.Rule then
            GK.Speed.Rule(RULE, nil, nil, nil)   -- remove it; Speed decides the pace again
            if GK.Speed.Status and GK.Speed.Status.enabled then
                GK.Speed.Update()
            else
                write(1)
            end
        else
            write(1)
        end
    end
    return Pause.State()
end

function Pause.State()
    return { paused = state.paused, by = state.by, engine = read(), floor = FLOOR }
end

--- Pause, do something, and put the pace back whatever happens - including an error, which is
--- re-raised after the world is let go. A run that dies mid-thought must not leave a frozen game.
function Pause.While(fn, by)
    local was = state.paused
    Pause.Set(true, by or "GK.Pause.While")
    local ok, result = pcall(fn)
    if not was then Pause.Set(false) end
    if not ok then error(result, 0) end
    return result
end

-- GK.Pause(true) / GK.Pause(false) reads better at a call site than GK.Pause.Set(true), and the
-- table is what holds State and While. Both spellings do the same thing.
setmetatable(Pause, { __call = function(_, paused, by) return Pause.Set(paused, by) end })

-- A load or a Lua reset loses this file's state while the engine keeps the scale it was given
-- (seen on 2026-11-13 with GK.Speed). So a game that was left frozen does not stay frozen for a
-- reason nobody can see: if nothing claims a pause, the game's own pace is put back.
if not state.paused and (read() or 1) <= FLOOR then write(1) end

return Pause
