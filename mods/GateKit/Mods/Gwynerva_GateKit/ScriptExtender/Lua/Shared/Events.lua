-- A short memory of what happened in the game, so a test (or an assistant) can ask
-- "what happened since I last looked" instead of staring at the screen.
--
--   GK.Note(kind, data)     add an entry
--   GK.Events(since)        entries with a number above `since` (0 or nil: everything kept)

local MAX = 500
local entries = {}
local counter = 0

function GK.Note(kind, data)
    counter = counter + 1
    entries[#entries + 1] = { n = counter, t = Ext.Utils.MonotonicTime(), kind = kind, data = data }
    if #entries > MAX then table.remove(entries, 1) end
end

function GK.Events(since)
    since = since or 0
    local out = {}
    for _, entry in ipairs(entries) do
        if entry.n > since then out[#out + 1] = entry end
    end
    return { last = counter, events = out }
end

Ext.Events.GameStateChanged:Subscribe(function(e)
    GK.Note("GameState", { from = tostring(e.FromState), to = tostring(e.ToState) })
end)
