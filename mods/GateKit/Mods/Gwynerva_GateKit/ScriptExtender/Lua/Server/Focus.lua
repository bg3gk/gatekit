-- GK.Focus (server side): shows whoever watches where the agent is looking and what it does.
--
-- An agent issues commands without a mouse, so nothing on screen says where a character is
-- about to walk or what it is about to hit. GK.Focus marks it with the game's own UI effects -
-- the same rings and beams the game draws under a player's cursor:
--
--   move     the destination beam of a move in a fight (VFX_UI_DestinationBeam_Movement_01)
--   ground   the destination ring of a move outside fights (VFX_UI_Destination_Exploration_01)
--   target   the circle under an attack or spell target (VFX_UI_Circle_AttackTarget_01)
--   object   the highlight ring under something to use, open, pick up, talk to
--            (VFX_UI_Circle_Highlight_01)
--   outline  characters and items get the agent's own violet, pulsing outline instead of the
--            target and object rings (Focus.Outlines = false gives the rings back)
--
-- A glance (Focus.Glance: what the agent examines or weighs) is the same ring for a moment.
-- One marker at a time: a new one replaces the last. Markers for actions (GK.Act jobs) appear
-- when the job starts and go when it ends; a glance goes by itself. (The game's map ping,
-- VFX_UI_MapPing_01, was tried for glances: it draws on the map only, not in the world.) The effects are cosmetic
-- and local to the screen: they change nothing in the game's rules (see docs/situation.md).
-- The client side of GK.Focus keeps the game's own pointer calm meanwhile.

local Focus = GK.Focus or {}
GK.Focus = Focus

Focus.Enabled = Focus.Enabled ~= false
Focus.MaxSeconds = 30      -- a marker never outlives this, whatever happens to its job

-- Effect resources (uuids of the game's own effects; the names are in the comments above)
Focus.Effects = {
    move = "ac3f9f5e-fe7d-e657-bf96-b6fed12320d2",
    ground = "61f092b1-c08f-d888-5b73-64d64dc8c69d",
    target = "8c0bcb14-2712-0892-471e-5878be1b88d2",
    object = "9169b8cf-2734-6adc-f5f8-a669df3fed5b",
}
Focus.GlanceSeconds = 2.5

-- What each kind of GK.Act job marks
Focus.ForKind = {
    Move = "walk", Cast = "aim", Attack = "target", Shove = "target", Throw = "aim", Help = "object",
    Use = "object", PickUp = "object", Loot = "object", Talk = "object", Lockpick = "object",
    Disarm = "object", Jump = "walk",
}

Focus.Generation = Focus.Generation or 0

local function try(fn, ...)
    local ok, result = pcall(fn, ...)
    if ok then return result end
end

local function isObject(aim) return type(aim) == "string" and aim ~= "" end

local function position(aim)
    if type(aim) == "table" then
        local x, y, z = aim.x or aim[1], aim.y or aim[2], aim.z or aim[3]
        if x and y and z then return { x, y, z } end
        return nil
    end
    if isObject(aim) then
        local ok, x, y, z = pcall(Osi.GetPosition, aim)
        if ok and x and y and z then return { x, y, z } end
    end
end

-- Characters and items the agent aims at get the agent's own outline instead of a ring: a
-- violet, pulsing contour drawn by the engine's outline renderer (client side of GK.Focus).
Focus.Outlines = Focus.Outlines ~= false
Focus.Channel = Focus.Channel or Ext.Net.CreateChannel(ModuleUUID, "GK_Focus")

local function outline(id, on)
    try(function() Focus.Channel:Broadcast({ id = id, on = on }) end)
end

--- Takes the current marker off the screen.
function Focus.Clear()
    if Focus.Handle then try(Osi.StopLoopEffect, Focus.Handle) end
    if Focus.Current and Focus.Current.outlined then outline(Focus.Current.outlined, false) end
    Focus.Handle, Focus.Current = nil, nil
    return { cleared = true }
end

--- Marks `aim` (an object's uuid or a position {x, y, z}) with a marker of `kind` (see above).
--- options.seconds: how long at most (Focus.MaxSeconds by default); options.job: the GK.Act job
--- the marker belongs to (it goes when that job ends).
function Focus.Show(kind, aim, options)
    options = options or {}
    if not Focus.Enabled then return { shown = false, why = "focus markers are off" } end
    local fx = Focus.Effects[kind]
    if not fx then return { shown = false, why = "unknown marker kind " .. tostring(kind) } end
    Focus.Clear()
    local handle, outlined
    local at = position(aim)
    if isObject(aim) and kind ~= "move" and kind ~= "ground" then
        if Focus.Outlines then
            outline(aim, true)
            outlined = aim
        else
            -- on the object itself: the ring follows it when it moves
            handle = try(Osi.PlayLoopEffect, aim, fx, "", 1.0)
        end
    end
    if not handle and not outlined then
        if not at then return { shown = false, why = "no position for the marker" } end
        handle = try(Osi.PlayLoopEffectAtPosition, fx, at[1], at[2], at[3], 1.0)
        if not handle then return { shown = false, why = "the effect did not start" } end
    end
    Focus.Generation = Focus.Generation + 1
    local generation = Focus.Generation
    Focus.Handle = handle
    Focus.Current = { kind = kind, aim = aim, at = at, job = options.job, outlined = outlined }
    local seconds = math.min(options.seconds or Focus.MaxSeconds, Focus.MaxSeconds)
    Ext.Timer.WaitFor(math.floor(seconds * 1000), function()
        if Focus.Generation == generation then Focus.Clear() end
    end)
    return { shown = true, kind = outlined and "outline" or kind, at = at }
end

--- A glance at `aim` (what the agent examines or weighs): a ring for Focus.GlanceSeconds. It
--- does not replace the marker of a running action.
function Focus.Glance(aim)
    if not Focus.Enabled then return { shown = false, why = "focus markers are off" } end
    if Focus.Current and Focus.Current.job then
        local job = GK.Act and GK.Act.JobList and GK.Act.JobList[Focus.Current.job]
        if job and job.status == "running" then return { shown = false, why = "an action is marked" } end
    end
    return Focus.Show(isObject(aim) and "object" or "ground", aim, { seconds = Focus.GlanceSeconds })
end

--- Markers on (true) or off (false).
function Focus.SetEnabled(on)
    Focus.Enabled = on and true or false
    if not Focus.Enabled then Focus.Clear() end
    return { enabled = Focus.Enabled }
end

function Focus.State()
    return { enabled = Focus.Enabled, current = Focus.Current }
end

local function hostile(who, target)
    return who and isObject(target) and (try(Osi.IsEnemy, who, target) == 1)
end

--- Called by GK.Act when a job starts: marks what the job aims at.
function Focus.JobStarted(job)
    if not Focus.Enabled or not job then return end
    local rule = Focus.ForKind[job.kind]
    local data = job.data or {}
    local aim = data.goal or data.to or data.target
    if not rule or aim == nil then return end
    local kind
    if rule == "walk" then
        kind = try(Osi.IsInCombat, job.who) == 1 and "move" or "ground"
    elseif rule == "aim" then
        if not isObject(aim) then kind = "ground"
        elseif aim == job.who then return
        else kind = hostile(job.who, aim) and "target" or "object" end
    else
        kind = rule
    end
    try(Focus.Show, kind, aim, { job = job.n })
end

--- Called by GK.Act when a job ends: its marker goes.
function Focus.JobFinished(job)
    if job and Focus.Current and Focus.Current.job == job.n then Focus.Clear() end
end

return Focus
