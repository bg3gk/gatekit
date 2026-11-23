-- The loading screen on the event log: what it showed, read after the fact from the start's
-- timeline of GateKit's Script Extender build (Ext.StartupVideos.Timeline, the fork's Docs/API.md,
-- "Startup Videos"). The loading screen runs while the client's Lua is being reset or not at all
-- (the start), so nothing here listens to it: at each boot of the client's Lua, and every few
-- ticks after, the rows the fork recorded since the last ones imported become rows of the event
-- log (family `state`, observations; never attention):
--   loading_wallpaper  what = "set": the loading screen's slideshow (gui::LSImageSequence) made
--                      ready, with its images in the order it will show them, and its times;
--                      what = "shown": the wallpaper on screen changed (the slideshow's clock, or
--                      a single image of the other looks); what = "gone": the wallpaper's element
--                      left the interface (its widget went), with the image it showed last
--   hint_shown         the loading screen drew a hint
-- Each carries `ms`, the timeline's time (milliseconds since the game process was created), and
-- `row`, the timeline's row. LoadingProgress rows are not imported: the bar's counts move tens of
-- times a load, and the game's own totals run past 100% (3/2, 7/6), so they say nothing a reader
-- of the log can use that `loading` / `loading_ended` (attention) do not; `gatekit timeline` has them.
-- Where the import stopped is kept in a file of the channel with the game process's name, so a
-- new Lua state of the same process goes on where the previous one stopped and a new process
-- starts from its first row.

local L = GK.EventLog
local MARK = "Gwynerva_GateKit/timeline_imported.json"
local EVERY = 15                     -- ticks between two reads (the read of no new row is cheap)

-- Data only (no functions under GK: every public function of the bridge has a class in
-- Shared/VerbClasses.json). row: the last timeline row imported; ticks: since the last read.
GK.LoadingScreenImport = GK.LoadingScreenImport or {}
local state = GK.LoadingScreenImport

local function available()
    return Ext.StartupVideos ~= nil and Ext.StartupVideos.Timeline ~= nil
end

-- Where the previous Lua state of this game process stopped (0 in a new process).
local function readMark()
    local ok, text = pcall(Ext.IO.LoadFile, MARK)
    if not ok or not text or text == "" then return 0 end
    local fine, mark = pcall(Ext.Json.Parse, text)
    if not fine or type(mark) ~= "table" then return 0 end
    if mark.process ~= L.Process then return 0 end
    return tonumber(mark.row) or 0
end

local function writeMark()
    pcall(Ext.IO.SaveFile, MARK, Ext.Json.Stringify({ process = L.Process, row = state.row }, { Beautify = false }))
end

-- One `loading_wallpaper` set row from the WallpaperSet rows of one OnLoaded (Index 1..Count).
local function appendSet(rows)
    local first = rows[1]
    local images = {}
    for _, e in ipairs(rows) do images[#images + 1] = e.Name end
    L.Append("loading_wallpaper", { what = "set", ms = first.Ms, row = first.Row, images = images,
                                    slot = first.Slot, fadeIn = first.FadeIn, fadeOut = first.FadeOut,
                                    owner = first.Owner, ownerData = first.OwnerData, visible = first.Visible })
end

-- The timeline's rows after state.row into the log. A slideshow's set is written once all of its
-- rows are there (the fork writes them one after the other, and a read may fall between them).
local function import()
    if not available() then return end
    local since = state.row or 0
    local ok, rows = pcall(Ext.StartupVideos.Timeline, since)
    if not ok or type(rows) ~= "table" then return end
    local count = tonumber(rows.Count)
    local list = rows
    if not count then return end
    if count < since then
        -- another record than the one the mark was taken in: from its first row
        state.row = 0
        return import()
    end
    local done = since
    local set, held
    local function one(e)
        if e.What == "WallpaperShown" or e.What == "WallpaperGone" then
            L.Append("loading_wallpaper", { what = e.What == "WallpaperGone" and "gone" or "shown", ms = e.Ms, row = e.Row, image = e.Name, index = e.Index,
                                            count = e.Count, look = e.Look, owner = e.Owner, ownerData = e.OwnerData, visible = e.Visible })
        elseif e.What == "HintShown" then
            L.Append("hint_shown", { ms = e.Ms, row = e.Row, key = e.Name, index = e.Index, count = e.Count })
        end
    end
    for _, e in ipairs(list) do
        if e.What == "WallpaperSet" then
            if tonumber(e.Index) == 1 then set, held = {}, {} end
            if set then
                set[#set + 1] = e
                if #set >= (tonumber(e.Count) or 0) then
                    appendSet(set)
                    for _, h in ipairs(held) do one(h) end
                    set, held = nil, nil
                end
            end
        elseif set then
            -- a row of another thread between two rows of a set: told after the set
            held[#held + 1] = e
        else
            one(e)
        end
        -- a set not complete yet is read again, from its first row, next time
        if not set then done = tonumber(e.Row) or done end
    end
    if done ~= state.row then
        state.row = done
        writeMark()
    end
end

if not state.subscribed then
    state.subscribed = true
    state.row = readMark()
    state.ticks = 0
    local fine, problem = pcall(import)
    if not fine then Ext.Utils.PrintWarning("[GateKit] the loading screen's rows: " .. tostring(problem)) end
    Ext.Events.Tick:Subscribe(function()
        state.ticks = (state.ticks or 0) + 1
        if state.ticks < EVERY then return end
        state.ticks = 0
        local ok, why = pcall(import)
        if not ok then Ext.Utils.PrintWarning("[GateKit] the loading screen's rows: " .. tostring(why)) end
    end)
end
