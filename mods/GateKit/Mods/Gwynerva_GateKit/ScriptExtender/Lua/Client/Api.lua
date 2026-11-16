-- Client side of the GateKit API: what the player sees - scenes, the controlled character.

local function uuid(entity)
    return entity and entity.Uuid and entity.Uuid.EntityUuid or nil
end

-- The characters this client controls, kept from the game's own events: eoc::ClientControl
-- created and destroyed, subscribed once here, on the main thread. A verb reads the kept handles
-- from any context, the UI's deferred one included, without asking the entity system for a list:
-- such a list read from the render thread while the main thread changes it came back empty.
GK.ControlledSet = GK.ControlledSet or { entities = {} }
local controlledSet = GK.ControlledSet
if not controlledSet.subscribed then
    controlledSet.subscribed = true
    local ok = pcall(function()
        Ext.Entity.OnCreate("ClientControl", function(entity) controlledSet.entities[tostring(entity)] = entity end)
        Ext.Entity.OnDestroy("ClientControl", function(entity) controlledSet.entities[tostring(entity)] = nil end)
    end)
    controlledSet.events = ok
    -- what is controlled already (a bridge reloaded inside a level), read here on the main thread,
    -- and again on the game's own event for a level running: subscriptions made as the bridge
    -- loads at game start, before a level, are not seen to fire (the first test of a fresh copy
    -- found no controlled character), so the set is taken from the entity system then, on the
    -- main thread, where its list is whole
    -- the set is made anew from the list: what a level left behind goes with it
    local function seed()
        local list = Ext.Entity.GetAllEntitiesWithComponent("ClientControl") or {}
        if #list == 0 then return end
        controlledSet.entities = {}
        for _, entity in ipairs(list) do controlledSet.entities[tostring(entity)] = entity end
    end
    seed()
    Ext.Events.GameStateChanged:Subscribe(function(e)
        if tostring(e.ToState) == "Running" then pcall(seed) end
    end)
    -- while a running level shows no controlled character yet (its ClientControl comes a moment
    -- after the state), the bridge's tick reads the list once a tick; nothing is read once one is kept
    if GK.Attention and GK.Attention.Pollers then
        GK.Attention.Pollers.controlled = function()
            if next(controlledSet.entities) == nil and tostring(Ext.Utils.GetGameState()) == "Running" then seed() end
        end
    end
end

--- The characters this client controls, from the game's ClientControl events: a list of
--- entities, in a stable order (by handle). Nothing is read from the entities here: a read from
--- the render thread while the main thread changes them has failed.
function GK.Controlled()
    local keys = {}
    for key in pairs(controlledSet.entities) do keys[#keys + 1] = key end
    table.sort(keys)
    local out = {}
    for i, key in ipairs(keys) do out[i] = controlledSet.entities[key] end
    return out
end

--- The client in a few words: game state, input mode, who is controlled.
function GK.Client()
    local controlled = {}
    for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ClientControl")) do
        controlled[#controlled + 1] = uuid(entity)
    end
    local scenes = 0
    for _ in pairs(Ext.System.ClientTimeline.Timelines) do scenes = scenes + 1 end
    return {
        gameState = tostring(Ext.Utils.GetGameState()),
        controllerMode = Ext.Utils.GetGlobalSwitches().ControllerMode ~= 0,
        controlled = controlled,
        liveTimelines = scenes,
        version = { extender = Ext.Utils.Version(), game = Ext.Utils.GameVersion() },
    }
end

--- Live timelines (dialogues, cutscenes, gameplay timelines).
--- With `tracks` = true each one also lists how many tracks of each kind it has.
function GK.Scenes(tracks)
    local out = {}
    for id, timeline in pairs(Ext.System.ClientTimeline.Timelines) do
        local entry = {
            id = tostring(id),
            phase = timeline.PhaseIndex,
            time = timeline.TimePlayed,
            duration = timeline.Duration,
            paused = timeline.IsPaused,
            gameplay = timeline.IsGameplayTimeline,
            dialogInstance = timeline.DialogInstanceId,
            actors = {},
        }
        for actor, entity in pairs(timeline.RegisteredActors) do
            entry.actors[#entry.actors + 1] = { actor = tostring(actor), dummy = tostring(entity) }
        end
        if tracks then
            entry.tracks = {}
            local components = timeline.Components
            for i = 1, #components do
                local kind = tostring(Ext.Types.GetObjectType(components[i])):gsub("^aspk::", ""):gsub("Component$", "")
                entry.tracks[kind] = (entry.tracks[kind] or 0) + 1
            end
        end
        out[#out + 1] = entry
    end
    return out
end

--- The tracks of one kind in a timeline, e.g. GK.Tracks(id, "TLShowWeapon"):
--- { id, actor, phase, start, finish, active } each.
function GK.Tracks(timelineId, kind)
    local out = {}
    for id, timeline in pairs(Ext.System.ClientTimeline.Timelines) do
        if tostring(id) == timelineId then
            local components = timeline.Components
            for i = 1, #components do
                local component = components[i]
                if tostring(Ext.Types.GetObjectType(component)):find(kind, 1, true) then
                    out[#out + 1] = {
                        id = tostring(component.ID),
                        actor = tostring(component.Actor.UUID),
                        phase = component.PhaseIndex,
                        start = component.StartTime,
                        finish = component.EndTime,
                        active = component.IsActive,
                    }
                end
            end
        end
    end
    return out
end

-- Fast scenes: dialogues and cutscenes are timelines, and a timeline has a playing speed.
-- With it raised, the spoken lines and camera work fly by and the dialogue arrives at the
-- next choice almost at once; nothing is skipped, so the story sees every node as usual.
local sceneSpeed = 1.0

--- Plays every dialogue and cutscene `speed` times faster (20 is a good value; 1 or nil
--- returns to normal). Stays on, also for scenes that start later, until changed.
function GK.FastScenes(speed)
    sceneSpeed = tonumber(speed) or 1.0
    for _, timeline in pairs(Ext.System.ClientTimeline.Timelines) do
        if not timeline.IsGameplayTimeline then timeline.PlayingSpeed = sceneSpeed end
    end
    return sceneSpeed
end

Ext.Events.Tick:Subscribe(function()
    if sceneSpeed == 1.0 then return end
    for _, timeline in pairs(Ext.System.ClientTimeline.Timelines) do
        if not timeline.IsGameplayTimeline and timeline.PlayingSpeed ~= sceneSpeed then
            timeline.PlayingSpeed = sceneSpeed
        end
    end
end)

-- Keys go through the game's own input system, so they need neither the window nor the
-- operating system and work the same under Proton. The game speaks SDL scan codes; GateKit's
-- own names ("escape", "enter", "f11", "i", "1") are translated to them here, and a scan code
-- name ("ESCAPE", "NUM_1", "KP_ENTER") passes through unchanged.
local scanCodes = {
    escape = "ESCAPE", enter = "RETURN", space = "SPACE", tab = "TAB",
    backspace = "BACKSPACE", delete = "DEL",
    left = "LEFT", up = "UP", right = "RIGHT", down = "DOWN",
}
for n = 1, 12 do scanCodes["f" .. n] = "F" .. n end

--- The SDL scan code name of a key name, or nil when there is no such key.
function GK.ScanCode(name)
    if type(name) ~= "string" or name == "" then return nil end
    local known = scanCodes[name:lower()]
    if known then return known end
    if #name == 1 then
        local one = name:upper()
        if one:match("%u") then return one end
        if one:match("%d") then return "NUM_" .. one end
    end
    local upper = name:upper()
    if Ext.Enums.SDLScanCode[upper] ~= nil then return upper end
    return nil
end

--- Presses a key inside the game's own input system. The name is GateKit's ("escape", "enter",
--- "f11", "i", "1") or an SDL scan code name ("ESCAPE", "NUM_1"); `modifiers` is an optional
--- SDL key modifier ("LShift", "LCtrl", ...). The window needs neither focus nor to be visible.
--- `hold` (seconds) keeps the key down that long and lets it go on a timer - what moving or turning
--- the camera needs: a press is a tap, and the camera did not move (N49 of the third coverage run,
--- where a runner held keys with InjectKeyDown, a sleep and InjectKeyUp). GK.Key(name, hold) works.
function GK.Key(name, modifiers, hold)
    if type(modifiers) == "number" and hold == nil then modifiers, hold = nil, modifiers end
    local code = GK.ScanCode(name)
    if not code then error("unknown key '" .. tostring(name) .. "'", 2) end
    if type(hold) == "number" and hold > 0 then
        local input = Ext.ClientInput
        if not (input and input.InjectKeyDown and input.InjectKeyUp) then input = Ext.Input end
        if not (input and input.InjectKeyDown and input.InjectKeyUp) then
            error("this Script Extender build cannot hold a key (no InjectKeyDown/InjectKeyUp)", 2)
        end
        input.InjectKeyDown(code, modifiers)
        Ext.Timer.WaitFor(math.floor(hold * 1000), function() input.InjectKeyUp(code, modifiers) end)
        return { key = code, held = hold }
    end
    Ext.ClientInput.InjectKeyPress(code, modifiers)
    return true
end

--- Asks the game for a picture of itself: the frame the renderer is about to show, copied out
--- before any window of the extender's is drawn over it, so the picture is the game alone and
--- taking it neither pauses nor moves anything. `path` is below the Script Extender folder of
--- the profile, as `Ext.IO.SaveFile` takes it, and the PNG appears there a few frames later -
--- `GK.ShotPending()` is false once it is written. The window needs neither focus nor to be
--- visible. False when the renderer cannot do it (so far only the DX11 one can) or when a
--- picture is already on its way. `overlay = true` asks for the frame after the extender's own
--- layer is drawn - its windows, GateKit's console, a picture of GK.Show - and is false on a
--- Script Extender build that cannot take that frame (GK.Features.imageFiles tells it).
function GK.Shot(path, overlay)
    if not Ext.ClientRender then return false end
    if overlay then
        if not GK.Features.imageFiles then return false end
        return Ext.ClientRender.Screenshot(path, true)
    end
    return Ext.ClientRender.Screenshot(path)
end

--- Whether a picture asked for with `GK.Shot` has still to be taken or written.
function GK.ShotPending()
    if not Ext.ClientRender then return false end
    return Ext.ClientRender.ScreenshotPending()
end

--- How fast this copy is allowed to draw, and whether the engine is throttling it for being in
--- the background. A table, or nil where the Script Extender build cannot reach it:
---   frameCap             frames a second asked for; 0 is no cap
---   effectiveFrameCap    frames a second in force right now
---   lostFocusFrameCap    the cap used while the window is not in front
---   microsecondsPerFrame what the engine's clock derives from the cap in force
---   hasFocus             whether the engine believes its window is the one in front
---   yieldOnLostFocus     the engine's own switch for throttling in the background
function GK.FrameCap()
    if not (Ext.ClientRender and Ext.ClientRender.FrameCap) then return nil end
    local cap = Ext.ClientRender.FrameCap()
    if not cap then return nil end
    return {
        frameCap = cap.FrameCap,
        effectiveFrameCap = cap.EffectiveFrameCap,
        lostFocusFrameCap = cap.LostFocusFrameCap,
        microsecondsPerFrame = cap.MicrosecondsPerFrame,
        hasFocus = cap.HasFocus,
        yieldOnLostFocus = cap.YieldOnLostFocus,
    }
end

--- Caps this copy at `fps` frames a second whether its window is in front or not. 0 lifts the cap,
--- and with it the throttle the engine otherwise puts a background copy under - which is why a
--- mirrored copy, or one behind another in a deck, is worth calling this on at all.
---
--- A copy nobody is watching is the point: it runs a test or lets an agent think, and ten
--- frames a second leave the machine to the copy that is being watched. The bridge still answers
--- on the game's own tick, so a very low cap costs up to one frame of latency.
---
--- False where the Script Extender build cannot reach it or the engine's layout is not the one it
--- knows.
function GK.SetFrameCap(fps)
    if not (Ext.ClientRender and Ext.ClientRender.SetFrameCap) then return false end
    return Ext.ClientRender.SetFrameCap(math.max(0, math.floor(tonumber(fps) or 0)))
end
