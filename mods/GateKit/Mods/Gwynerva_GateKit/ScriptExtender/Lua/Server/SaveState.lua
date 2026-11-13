-- GK.SaveState: the bridge's own memory travels inside the game's save.
--
-- What the bridge learns while an agent plays lives in Lua tables that a load throws away: what
-- the party saw and remembers (GK.See.Memory, the "known" scope), the game log (GK.GameLog), what
-- was heard (GK.Heard), the immersive lines (GK.Act.Narration), the cheats used in set-up
-- (GK.Cheat.Log) and whether the focus markers are on. Whenever the game saves - a quick save, a
-- save from the menu, an autosave, by an agent or by a person - those tables are written into the
-- save itself (the Script Extender's PersistentVars of the bridge mod, one JSON text under
-- GKState), and when that save is loaded they come back. So a load returns the bridge to the
-- moment of the save, together with the world.
--
--   GK.SaveState.Snapshot()  the tables as they are now (what a save would carry)
--   GK.SaveState.Write()     writes them into PersistentVars now; returns the size in bytes
--   GK.SaveState.Restore()   puts back what the loaded save carried (done on SessionLoaded)
--   GK.SaveState.Info()      what was written and restored last
--
-- Server side. Saves made before this module existed carry nothing: the tables start empty.

GK.SaveState = GK.SaveState or {}
local State = GK.SaveState

State.Key = "GKState"
State.Version = 1

local function try(fn, ...)
    local ok, result = pcall(fn, ...)
    if ok then return result end
end

local function vars()
    local env = Mods and Mods.GateKit
    if not env then return nil end
    env.PersistentVars = env.PersistentVars or {}
    return env.PersistentVars
end

local function levelName()
    local host = try(function() return Osi.GetHostCharacter() end)
    return host and try(Osi.GetRegion, host) or nil
end

function State.Snapshot()
    local See, Log, Heard, Act, Cheat, Focus = GK.See, GK.GameLog, GK.Heard, GK.Act, GK.Cheat, GK.Focus
    return {
        version = State.Version,
        level = levelName(),
        memory = See and See.Memory or nil,
        log = Log and { entries = Log.Entries, next = Log.Next, hp = Log.Hp } or nil,
        heard = Heard and { lines = Heard.Lines, next = Heard.Next } or nil,
        narration = Act and Act.Narration or nil,
        cheats = Cheat and Cheat.Log or nil,
        focus = Focus and Focus.Enabled,
    }
end

function State.Write()
    local pv = vars()
    if not pv then return nil end
    local ok, text = pcall(Ext.Json.Stringify, State.Snapshot(), { Beautify = false, AvoidRecursion = true })
    if not ok then
        State.Written = { error = tostring(text) }
        return nil
    end
    pv[State.Key] = text
    State.Written = { bytes = #text, level = levelName() }
    return #text
end

-- The logs are keyed by their running number with the oldest dropped, so JSON writes them as
-- objects and reads the keys back as strings: turn them into numbers again.
local function numeric(t)
    local out = {}
    for k, v in pairs(type(t) == "table" and t or {}) do out[tonumber(k) or k] = v end
    return out
end

local function count(t)
    local n = 0
    for _ in pairs(t or {}) do n = n + 1 end
    return n
end

local function remembered(memory)
    local n = 0
    for _, level in pairs(type(memory) == "table" and memory or {}) do n = n + count(level) end
    return n
end

function State.Restore()
    local pv = vars()
    local text = pv and pv[State.Key]
    if type(text) ~= "string" or text == "" then
        State.Restored = { nothing = true }
        return State.Restored
    end
    local ok, data = pcall(Ext.Json.Parse, text)
    if not ok or type(data) ~= "table" then
        State.Restored = { error = "the saved state could not be read" }
        return State.Restored
    end
    local See, Log, Heard, Act, Cheat, Focus = GK.See, GK.GameLog, GK.Heard, GK.Act, GK.Cheat, GK.Focus
    if See and type(data.memory) == "table" then See.Memory = data.memory end
    if Log and type(data.log) == "table" then
        Log.Entries = numeric(data.log.entries)
        Log.Next = tonumber(data.log.next) or 1
        Log.Hp = data.log.hp or {}
    end
    if Heard and type(data.heard) == "table" then
        Heard.Lines = numeric(data.heard.lines)
        Heard.Next = tonumber(data.heard.next) or 1
    end
    if Act and type(data.narration) == "table" then Act.Narration = data.narration end
    if Cheat and type(data.cheats) == "table" then Cheat.Log = data.cheats end
    if Focus and data.focus ~= nil then Focus.Enabled = data.focus and true or false end
    State.Restored = { bytes = #text, level = data.level, remembered = remembered(data.memory),
                       logEntries = count(Log and Log.Entries), heardLines = count(Heard and Heard.Lines) }
    return State.Restored
end

function State.Info()
    return { written = State.Written, restored = State.Restored }
end

if not State.Subscribed then
    State.Subscribed = true
    -- late-bound, so a reloaded file takes over
    Ext.Events.GameStateChanged:Subscribe(function(e)
        if tostring(e.ToState) == "Save" then try(GK.SaveState.Write) end
    end)
    Ext.Events.SessionLoaded:Subscribe(function()
        try(GK.SaveState.Restore)
    end)
end

return State
