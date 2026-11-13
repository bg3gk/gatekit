-- The GateKit console: a window inside the game. F11 (changeable) or `!gk` in the Script
-- Extender console opens it.
--
--   Log tab       what Lua printed on both sides, filtered by side, level, mod and text;
--                 under it the input line: Lua for the client, the server or the UI context.
--   Watch tab     expressions re-evaluated once a second.
--   Settings tab  the key, what the game may still receive while the window is open.
--   Actions menu  one-click calls.
--
-- Everything the window does is also a function (GK.Console*), so a test or an assistant
-- can drive and read the console without looking at the screen. See docs/api.md.

local DIR = "Gwynerva_GateKit/"
local SETTINGS_FILE = DIR .. "console.json"
local LOG_FILE = DIR .. "console_log.json"
local TRANSCRIPT_FILE = DIR .. "console_transcript.log"
local COPY_FILE = DIR .. "console_copy.txt"

local PREFIX = "local GK = Mods.GateKit.GK\n"
local MAX_KEPT = 3000        -- log entries kept
local MAX_DRAWN = 250        -- entries drawn in the coloured view
local MAX_LINE = 3000        -- bytes of one entry drawn in the coloured view
local MAX_COPY = 400000      -- bytes put into the Select view
local MAX_HISTORY = 200
local SUGGESTIONS = 8
local REFRESH_MS = 100
local LEVELS = { "Debug", "Info", "Warning", "Error" }
local RANK = { Debug = 1, Info = 2, Warning = 3, Error = 4 }
local TARGETS = { "client", "server", "ui" }
local FONT_FACTOR = { Tiny = 0.67, Small = 0.78, Medium = 0.89, Default = 1, Large = 1.11, Big = 1.22 }
local Overlay = GK.Overlay   -- theme, text size, the key, input blocking: shared by every GateKit window

-- Settings kept between runs (console.json).
local S = {
    target = "client",
    editor = false,
    sides = { client = true, server = true },
    levels = { Debug = true, Info = true, Warning = true, Error = true },
    source = "",          -- "" = every source; a mod name; or "[tag]"
    filter = "",
    history = {},
    watches = {},         -- { { side, code } }
    open = false,
}

local now = Ext.Utils.MonotonicTime

local function readJson(file)
    local ok, text = pcall(Ext.IO.LoadFile, file)
    if not ok or not text or text == "" then return nil end
    local parsed, value = pcall(Ext.Json.Parse, text)
    return parsed and type(value) == "table" and value or nil
end

local function writeJson(file, value)
    local ok, text = pcall(Ext.Json.Stringify, value, { Beautify = true })
    if ok then pcall(Ext.IO.SaveFile, file, text) end
end

do
    local saved = readJson(SETTINGS_FILE)
    if saved then
        for key, value in pairs(saved) do
            if type(S[key]) == type(value) or S[key] == nil then S[key] = value end
        end
        for _, level in ipairs(LEVELS) do if S.levels[level] == nil then S.levels[level] = true end end
        for _, side in ipairs({ "client", "server" }) do if S.sides[side] == nil then S.sides[side] = true end end
    end
end

-- A file written by this same game process a moment ago: only then is its volatile part
-- (was the window open, the log itself) worth restoring. The clock starts with the process.
local function fresh(stamp, within)
    local t = now()
    return type(stamp) == "number" and stamp <= t and t - stamp < within
end
local reopen = S.open and fresh(S.stamp, 20000)

local settingsDirty = false
local function touch() settingsDirty = true end

-- --- The log ------------------------------------------------------------------------------

local log = {}
local counter = 0
local ticket = 0          -- numbers the commands run from the console
local counts = { Debug = 0, Info = 0, Warning = 0, Error = 0 }
local sources, sourceList = {}, {}    -- name -> entries seen; names in order of appearance
local logDirty = true
local follow = true       -- the view tracks the newest line
local frozenAt = 0        -- with follow off: entries after this number are not shown yet
local view = "color"      -- "color" | "select"

-- Lua here has no wall clock (no os.date): lines are stamped with the time since the game
-- process started, as minutes:seconds.
local function clock()
    local seconds = now() / 1000
    return string.format("%3d:%04.1f", math.floor(seconds / 60), seconds % 60)
end

local function noteSource(name)
    if not sources[name] then
        sources[name] = 0
        sourceList[#sourceList + 1] = name
        sourceList.changed = true
    end
    sources[name] = sources[name] + 1
end

local function add(entry)
    local last = log[#log]
    if last and not entry.kind and not last.kind and last.text == entry.text and last.side == entry.side
        and last.level == entry.level and last.source == entry.source then
        last.count = (last.count or 1) + 1
        last.clock = clock()
        logDirty = true
        return last
    end
    counter = counter + 1
    entry.n = counter
    entry.clock = clock()
    if not RANK[entry.level] then entry.level = "Info" end
    log[#log + 1] = entry
    counts[entry.level] = counts[entry.level] + 1
    noteSource(entry.source)
    if entry.tag then noteSource("[" .. entry.tag .. "]") end
    if #log > MAX_KEPT + 200 then
        local kept = {}
        for i = #log - MAX_KEPT + 1, #log do kept[#kept + 1] = log[i] end
        log = kept
    end
    logDirty = true
    return entry
end

GK.LogSinks[#GK.LogSinks + 1] = add
GK.LogRetracted = function() logDirty = true end

Ext.Net.CreateChannel(ModuleUUID, "GK_Log", function(msg)
    if (msg.dropped or 0) > 0 then
        add({ side = "server", level = "Warning", source = "Console",
            text = "(" .. msg.dropped .. " server lines were dropped: more arrived than could be sent)" })
    end
    for _, entry in ipairs(msg.entries or {}) do add(entry) end
end)

-- The log of a moment ago survives a Lua reload (hot reload restarts the Lua state).
do
    local saved = readJson(LOG_FILE)
    if saved and fresh(saved.stamp, 60000) then
        for _, entry in ipairs(saved.entries or {}) do
            entry.n = nil
            local keptClock = entry.clock
            local added = add(entry)
            added.clock = keptClock or added.clock
            if type(entry.ticket) == "number" and entry.ticket > ticket then ticket = entry.ticket end
        end
        add({ side = "client", level = "Debug", source = "Console", text = "--- Lua reloaded ---" })
    end
end

local function matcher(f)
    local sides, levels, source = f.sides, f.levels, f.source
    local wanted, unwanted = {}, {}
    for term in (f.filter or ""):gmatch("%S+") do
        if term:sub(1, 1) == "-" and #term > 1 then
            unwanted[#unwanted + 1] = term:sub(2):lower()
        else
            wanted[#wanted + 1] = term:lower()
        end
    end
    local textFilter = #wanted + #unwanted > 0
    return function(entry)
        -- a Script Extender line the server claimed as its own print after all (ConsoleLog.lua)
        if entry.retracted then return false end
        if sides and not sides[entry.side] then return false end
        if levels and not levels[entry.level] then return false end
        if source and source ~= "" then
            if source:sub(1, 1) == "[" then
                if not entry.tag or "[" .. entry.tag .. "]" ~= source then return false end
            elseif entry.source ~= source then
                return false
            end
        end
        if f.kind and entry.kind ~= f.kind then return false end
        if f.ticket and entry.ticket ~= f.ticket then return false end
        if textFilter then
            local hay = (entry.source .. " " .. entry.text):lower()
            for _, term in ipairs(wanted) do if not hay:find(term, 1, true) then return false end end
            for _, term in ipairs(unwanted) do if hay:find(term, 1, true) then return false end end
        end
        return true
    end
end

--- The newest `limit` entries that pass, oldest first. `upTo`: ignore entries numbered above it.
local function select(passes, limit, upTo, since)
    local picked = {}
    for i = #log, 1, -1 do
        local entry = log[i]
        if since and entry.n <= since then break end
        if (not upTo or entry.n <= upTo) and passes(entry) then
            picked[#picked + 1] = entry
            if #picked >= limit then break end
        end
    end
    local out = {}
    for i = #picked, 1, -1 do out[#out + 1] = picked[i] end
    return out
end

local function lineOf(entry)
    local head = entry.clock .. (entry.side == "server" and " S " or " C ")
    local body = entry.text
    if not entry.kind and not entry.tag and entry.source ~= "(global)" then
        body = "[" .. entry.source .. "] " .. body
    end
    if (entry.count or 1) > 1 then body = body .. "   (x" .. entry.count .. ")" end
    return head .. body
end

-- The colours of the log come from Weave's theme (ui/theme.css of the Weave mod: --log-cmd, --log-error, ...).
local COLORS = {}
local function loadColors()
    local color = Weave.Color
    COLORS.cmd = color("--log-cmd", { 0.33, 0.46, 0.5, 1 })
    COLORS.result = color("--log-result", { 0.42, 0.5, 0.42, 1 })
    COLORS.Error = color("--log-error", { 0.5, 0.31, 0.3, 1 })
    COLORS.Warning = color("--log-warning", { 0.5, 0.45, 0.28, 1 })
    COLORS.Debug = color("--log-debug", { 0.38, 0.38, 0.38, 1 })
    COLORS.client = color("--log-client", { 0.48, 0.48, 0.48, 1 })
    COLORS.server = color("--log-server", { 0.42, 0.46, 0.5, 1 })
end
loadColors()
local tint      -- colours a widget from this palette; defined with the window's builders

local function colorKey(entry)
    if entry.level == "Error" or entry.level == "Warning" then return entry.level end
    if entry.kind then return entry.kind end
    if entry.level == "Debug" then return "Debug" end
    return entry.side
end

-- --- Running code -------------------------------------------------------------------------

local serverRun = Ext.Net.CreateChannel(ModuleUUID, "GK_Run")
local serverReset = Ext.Net.CreateChannel(ModuleUUID, "GK_Reset")
local watchValues = {}    -- index -> text
local watchGeneration = 0

local function transcript(text)
    pcall(Ext.IO.AppendFile, TRANSCRIPT_FILE, text .. "\n")
end

local function addResult(side, target, number, ok, text)
    if not ok then text = GK.ShortTrace(text) end
    add({ side = side, level = ok and "Info" or "Error", source = "Console", kind = "result",
        ticket = number, target = target, text = text })
    transcript(text)
end

-- Where other parts of the overlay (the mod panels in Client/Panels.lua) hook in:
--   built   functions(window, tabBar, tabs) called whenever the window has been built
--   second  functions() called once a second while the window is open
GK.ConsoleHooks = { built = {}, second = {} }

local asks, askCounter = {}, 0

--- Evaluates code on a side without a word in the log and hands the outcome to
--- `callback(ok, text, value)`: `text` as the console would print it, `value` the returned
--- value itself (plain data). On the client the callback runs at once, for the server when
--- the answer arrives.
function GK.ConsoleAsk(side, code, callback)
    if side == "server" then
        askCounter = askCounter + 1
        local id = "ask" .. askCounter
        asks[id] = callback
        local sent = pcall(function() serverRun:SendToServer({ ticket = id, code = code, kind = "ask" }) end)
        if not sent then
            asks[id] = nil
            callback(false, "ERROR: no server to send to (not in a game?)", nil)
        end
    else
        local answer = GK.Run(PREFIX .. code)
        callback(answer.ok, answer.ok and GK.Format(answer) or GK.ShortTrace(GK.Format(answer)), answer.result)
    end
end

Ext.Net.CreateChannel(ModuleUUID, "GK_Result", function(msg)
    if msg.kind == "ask" then
        local callback = asks[msg.ticket]
        asks[msg.ticket] = nil
        if callback then callback(msg.ok ~= false, tostring(msg.text), msg.value) end
        return
    end
    if msg.kind == "watch" then
        local generation, index = tostring(msg.ticket):match("^(%d+):(%d+)$")
        if tonumber(generation) == watchGeneration then watchValues[tonumber(index)] = tostring(msg.text) end
        return
    end
    addResult("server", "server", msg.ticket, msg.ok ~= false, tostring(msg.text))
end)

local function runLocal(code)
    GK.LogContext = "Console"
    local answer = GK.Run(PREFIX .. code)
    GK.LogContext = nil
    return answer
end

--- Runs code as the console does: echoes it, runs it on `target` ("client", "server", "ui"),
--- and logs the result. `@name.lua` runs a file from the Script Extender folder in the user
--- profile. Returns the ticket number that the echo and the result carry.
function GK.ConsoleRun(target, code)
    target = target or S.target
    code = tostring(code or "")
    local shown = code
    local file = code:match("^@%s*(.-)%s*$")
    if file then
        local ok, text = pcall(Ext.IO.LoadFile, file)
        code = ok and text or nil
    end
    -- `!name args`: a console command some mod registered with Ext.RegisterConsoleCommand
    local command = code and code:match("^!%s*(.+)$")
    if command then code = ("Ext.Events.DoConsoleCommand:Throw({ Command = %q })"):format(command) end
    ticket = ticket + 1
    local number = ticket
    local side = target == "server" and "server" or "client"
    add({ side = side, level = "Info", source = "Console", kind = "cmd", ticket = number, target = target,
        text = "> " .. (target == "ui" and "(ui) " or "") .. shown })
    transcript(clock() .. " [" .. target .. "] > " .. shown)
    follow = true
    if not code then
        addResult(side, target, number, false, "ERROR: no file " .. tostring(file) .. " in the Script Extender folder")
    elseif target == "server" then
        local sent = pcall(function() serverRun:SendToServer({ ticket = number, code = code, kind = "run" }) end)
        if not sent then addResult(side, target, number, false, "ERROR: no server to send to (not in a game?)") end
    elseif target == "ui" then
        Ext.UI.Defer(function()
            local answer = runLocal(code)
            addResult(side, target, number, answer.ok, GK.Format(answer))
        end)
    else
        local answer = runLocal(code)
        addResult(side, target, number, answer.ok, GK.Format(answer))
    end
    return number
end

-- --- History ------------------------------------------------------------------------------

local historyAt = nil     -- index while walking the history; nil = at the line being typed
local draft = ""          -- that line, kept while walking

local function remember(code)
    for i = #S.history, 1, -1 do
        if S.history[i] == code then table.remove(S.history, i) end
    end
    S.history[#S.history + 1] = code
    while #S.history > MAX_HISTORY do table.remove(S.history, 1) end
    historyAt = nil
    touch()
end

-- --- The window ---------------------------------------------------------------------------

local ui                  -- widgets, once built
local window
local inputSeen = ""      -- the input text last looked at
local completion = { base = "", prefix = "", matches = {}, more = 0, text = "" }
local completionId = 0
local status = ""         -- a one-line message shown beside the counters
local selectText = ""
local requestedSize, scale

-- IMGUI gives Lua no way to place the cursor. After the console rewrites the line being
-- edited (a completion), the cursor is left where IMGUI last had a selection - usually the
-- start - and after a click on a suggestion the whole line comes back selected. Typing must
-- continue behind the completed word all the same, so until the user edits the line in any
-- other way, what gets typed in front of the completed text (or over it) is moved behind it.
--
-- All of that is the stock Script Extender. On the GateKit's own build (docs/se-fork.md) the
-- field reports and takes the cursor, Up / Down and Tab arrive as callbacks, the log's scroll
-- position can be read and there is a clipboard; `can` says which of these this build has.
local sticky, stickySelected = nil, false
local build, setFont, showOverlay, buildEventsTab, pollEvents
local fontSet = false
local rescrollAt
local can = { history = false, completion = false, cursor = false, scroll = false,
    clipboard = GK.Features.clipboard, consoleMessages = GK.Features.consoleMessages,
    injectText = GK.Features.injectText }
local pendingTab          -- { head, tail }: a Tab waiting for the server's list of names
local scrollSeen          -- { y, atEnd }: the log's scroll position when last looked at
local scrollHold = 0      -- no reading of the scroll position before this time

local serverComplete = Ext.Net.CreateChannel(ModuleUUID, "GK_Complete")

local function field()
    return S.editor and ui.editor or ui.input
end

local function inputText()
    if not ui then return inputSeen end
    local ok, text = pcall(function() return field().Text end)
    return ok and text or ""
end

--- `cursor`: where the cursor goes (a byte offset; the end by default). Only the GateKit's
--- Script Extender build can place it.
local function setInput(text, focus, cursor)
    inputSeen = text
    if not ui then return end
    local target = field()
    target.Text = text
    if focus and window.Open then target:Activate() end
    if can.cursor then
        -- the text first, then the cursor: both are applied on the next frame
        local at = math.max(0, math.min(cursor or #text, #text))
        pcall(function()
            target:SetSelection(at, at)
            target.CursorPos = at
        end)
    end
end

local function showSuggestions()
    if not ui then return end
    local matches = completion.matches
    for i = 1, SUGGESTIONS do
        local button = ui.suggest[i]
        local match = matches[i]
        if match then
            button.Label = match .. "##dk_s" .. i
            button.Visible = true
        else
            button.Visible = false
        end
    end
    local rest = #matches - SUGGESTIONS + (completion.more or 0)
    local note = ""
    if #matches == 0 then
        note = S.editor and "Editor: Enter adds a line, Run executes."
            or (can.history and "Enter runs. Tab completes. Up / Down: earlier commands. @file.lua runs a file."
                or "Enter runs. Tab completes. @file.lua runs a file.")
    elseif rest > 0 then
        note = "+" .. rest .. " more"
    end
    -- what the first match is (Osiris: kind and parameter types)
    if #matches > 0 and completion.hint then note = note .. (note ~= "" and "    " or "") .. completion.hint end
    ui.suggestMore.Label = note
    ui.suggestMore.SameLine = #matches > 0
end

local function setCompletion(found, text)
    found.text = text
    completion = found
    showSuggestions()
end

local applyTab

Ext.Net.CreateChannel(ModuleUUID, "GK_Completions", function(msg)
    if msg.id ~= completionId then return end
    msg.matches = msg.matches or {}
    setCompletion(msg, completion.pendingText or "")
    local waiting = pendingTab
    pendingTab = nil
    if waiting and waiting.head == completion.text then applyTab(waiting.head, waiting.tail) end
end)

local function requestCompletion(text)
    completionId = completionId + 1
    pendingTab = nil
    if S.target == "server" then
        completion = { base = "", prefix = "", matches = {}, more = 0, text = text, pendingText = text }
        local id = completionId
        pcall(function() serverComplete:SendToServer({ id = id, text = text }) end)
    else
        setCompletion(GK.Complete(text), text)
    end
end

--- Completes the end of `text` with `match` (or, without one, as far as the matches agree).
local function completed(text, match)
    local found = completion
    if found.text ~= text or #found.matches == 0 then return text end
    local word = match or (#found.matches == 1 and found.matches[1]) or GK.CommonPrefix(found.matches)
    if #word < #found.prefix then return text end
    return text:sub(1, #text - #found.prefix) .. word
end

-- Tab with the cursor known (the GateKit's Script Extender build): the name in front of the
-- cursor is completed, what stands behind the cursor stays, the cursor ends up behind the name.
applyTab = function(head, tail)
    local found = completion
    if found.text ~= head or #found.matches == 0 then return end
    local word = (#found.matches == 1 and found.matches[1]) or GK.CommonPrefix(found.matches)
    if #word < #found.prefix then return end
    local newHead = head:sub(1, #head - #found.prefix) .. word
    if newHead == head then return end
    setInput(newHead .. tail, false, #newHead)
    if tail == "" then requestCompletion(newHead) end
end

local function tabAt(cursor)
    local text = inputText()
    cursor = math.max(0, math.min(tonumber(cursor) or #text, #text))
    local head, tail = text:sub(1, cursor), text:sub(cursor + 1)
    if completion.text == head and not completion.pendingText then
        applyTab(head, tail)
    elseif S.target == "server" then
        requestCompletion(head)
        pendingTab = { head = head, tail = tail }     -- applied when the server's names arrive
    else
        requestCompletion(head)
        applyTab(head, tail)
    end
end

local function submit(text)
    text = (text or inputText()):gsub("\t", "")
    local code = text:match("^%s*(.-)%s*$")
    if code == "" then
        if ui then field():Activate() end
        return nil
    end
    remember(code)
    setInput("", true)
    completion = { base = "", prefix = "", matches = {}, more = 0, text = "" }
    showSuggestions()
    return GK.ConsoleRun(S.target, code)
end

--- `typing`: the step comes from the field itself (Up / Down), which then keeps the focus.
local function historyStep(step, typing)
    local count = #S.history
    if count == 0 then return end
    if historyAt == nil then
        if step > 0 then return end
        draft = inputText()
        historyAt = count
    else
        historyAt = historyAt + step
        if historyAt < 1 then historyAt = 1 end
        if historyAt > count then
            historyAt = nil
            setInput(draft, not typing)
            return
        end
    end
    setInput(S.history[historyAt], not typing)
end

local function filters()
    return { sides = S.sides, levels = S.levels, source = S.source, filter = S.filter }
end

local function buildSelectText(everything)
    local entries = select(matcher(filters()), everything and MAX_KEPT or MAX_DRAWN * 4, not follow and frozenAt or nil)
    local lines, size = {}, 0
    for i = #entries, 1, -1 do
        local line = lineOf(entries[i])
        size = size + #line + 1
        if size > MAX_COPY then break end
        lines[#lines + 1] = line
    end
    local ordered = {}
    for i = #lines, 1, -1 do ordered[#ordered + 1] = lines[i] end
    return table.concat(ordered, "\n")
end

local function setView(mode)
    view = mode
    if mode == "select" then
        -- A text being selected must not move: the Select view is a snapshot.
        if follow then follow = false frozenAt = counter end
        selectText = buildSelectText(false)
        if ui then ui.select.Text = selectText end
    end
    if ui then
        ui.child.Visible = mode == "color"
        ui.select.Visible = mode == "select"
        ui.selectBox.Checked = mode == "select"
        ui.followBox.Checked = follow
    end
    logDirty = true
end

local function setFollow(on)
    follow = on and true or false
    if follow then
        if view == "select" then setView("color") end
    else
        frozenAt = counter
    end
    if ui then ui.followBox.Checked = follow end
    scrollSeen, scrollHold = nil, now() + 300
    logDirty = true
end

-- With the scroll position readable (the GateKit's Script Extender build) the wheel does what
-- the Follow box does: scrolling up makes the view stand still, scrolling back to the end
-- makes it follow again.
local function watchScroll()
    if not can.scroll or view ~= "color" or now() < scrollHold then return end
    local ok, y, max = pcall(function() return ui.child.LastScroll[2], ui.child.LastScrollMax[2] end)
    if not ok or not y or not max then return end
    local atEnd = y >= max - 4
    local seen = scrollSeen
    scrollSeen = { y = y, atEnd = atEnd }
    if not seen then return end
    if follow then
        -- new lines only ever push the position down; it went up, so the user scrolled
        if y < seen.y - 4 and not atEnd then setFollow(false) end
    elseif atEnd and not seen.atEnd then
        setFollow(true)
    end
end

local function layout()
    if not ui then return end
    -- room under the log for the suggestions and the input; it grows with the text size
    local factor = FONT_FACTOR[Overlay.Get("font")] or 1
    local footer = -math.floor((S.editor and 150 + 46 * factor or 64 * factor) + 0.5)
    ui.child.Size = { 0, footer }
    ui.select.SizeHint = { -1, footer }
    ui.input.Visible = not S.editor
    ui.editor.Visible = S.editor
    ui.editorBox.Checked = S.editor
end

local function sourceOptions()
    local options = { "All sources" }
    local tags = {}
    for _, name in ipairs(sourceList) do
        if name:sub(1, 1) == "[" then tags[#tags + 1] = name else options[#options + 1] = name end
    end
    for _, name in ipairs(tags) do options[#options + 1] = name end
    if S.source ~= "" and not sources[S.source] then options[#options + 1] = S.source end
    return options
end

local function refreshSources()
    if not ui then return end
    local options = sourceOptions()
    ui.sourceOptions = options
    ui.source.Options = options
    local index = 0
    for i, name in ipairs(options) do if name == S.source then index = i - 1 end end
    ui.source.SelectedIndex = index
    sourceList.changed = false
end

local shownCount = 0

local function refresh()
    if not ui then return end
    if sourceList.changed then refreshSources() end
    local entries = select(matcher(filters()), MAX_DRAWN, not follow and frozenAt or nil)
    shownCount = #entries
    if view == "color" then
        for i = 1, MAX_DRAWN do
            local widget, entry = ui.lines[i], entries[i]
            local cache = ui.lineCache[i]
            if entry then
                local line = lineOf(entry)
                if #line > MAX_LINE then
                    line = GK.CutUtf8(line, MAX_LINE) .. " ... (+" .. (#line - MAX_LINE) .. " bytes: Select view or GK.ConsoleLog)"
                end
                local key = colorKey(entry)
                if cache.text ~= line then widget.Label = line cache.text = line end
                if cache.color ~= key then widget:SetColor("Text", COLORS[key]) cache.color = key end
                if not cache.visible then widget.Visible = true cache.visible = true end
            elseif cache.visible then
                widget.Visible = false
                cache.visible = false
            end
        end
        if follow then ui.child:SetScroll({ 0, 10000000 }) end
    end
    local waiting = follow and 0 or (counter - frozenAt)
    local parts = { shownCount .. " shown of " .. #log }
    if counts.Error > 0 then parts[#parts + 1] = counts.Error .. " errors" end
    if counts.Warning > 0 then parts[#parts + 1] = counts.Warning .. " warnings" end
    if waiting > 0 then parts[#parts + 1] = "paused, +" .. waiting .. " new" end
    if status ~= "" then parts[#parts + 1] = status end
    ui.status.Label = table.concat(parts, "  |  ")
    logDirty = false
end

local function clearLog()
    log = {}
    counts = { Debug = 0, Info = 0, Warning = 0, Error = 0 }
    frozenAt = counter
    status = ""
    if view == "select" then selectText = "" if ui then ui.select.Text = "" end end
    logDirty = true
end

local function lineCount(text)
    if text == "" then return 0 end
    local _, breaks = text:gsub("\n", "")
    return breaks + 1
end

--- Copies the filtered log. With a clipboard (the GateKit's Script Extender build) the text
--- goes straight there; without one it is put into the Select view for Ctrl+A, Ctrl+C.
--- Either way it is also saved to console_copy.txt.
local function copyAll()
    local text = buildSelectText(true)
    pcall(Ext.IO.SaveFile, COPY_FILE, text)
    local copied = false
    if can.clipboard then
        local ok, stored = pcall(Ext.IMGUI.SetClipboardText, text)
        copied = ok and stored ~= false
    end
    if copied then
        status = "copied " .. lineCount(text) .. " lines to the clipboard"
    elseif ui then
        setView("select")
        selectText = text
        ui.select.Text = text
        ui.select:Activate()
        status = "Select view: press Ctrl+A, Ctrl+C (also saved to Script Extender/" .. COPY_FILE .. ")"
    end
    logDirty = true
    return { lines = lineCount(text), bytes = #text, clipboard = copied, file = COPY_FILE }
end

-- Watches

local function rebuildWatches()
    if not ui then return end
    for _, row in ipairs(ui.watchRows) do pcall(function() row.group:Destroy() end) end
    ui.watchRows = {}
    for index, watch in ipairs(S.watches) do
        local group = ui.watchList:AddGroup("dk_watch_" .. index)
        local remove = group:AddButton("x##dk_wx" .. index)
        remove.OnClick = function() GK.ConsoleUnwatch(index) end
        local name = group:AddText((watch.side == "server" and "S  " or "C  ") .. watch.code)
        name.SameLine = true
        tint(name, "cmd")
        local value = group:AddText(watchValues[index] or "...")
        value.TextWrapPos = 0
        ui.watchRows[index] = { group = group, value = value }
    end
    ui.watchEmpty.Visible = #S.watches == 0
end

local function evaluateWatches()
    for index, watch in ipairs(S.watches) do
        if watch.side == "server" then
            local id = watchGeneration .. ":" .. index
            pcall(function()
                serverRun:SendToServer({ ticket = id, code = "return " .. watch.code, kind = "watch" })
            end)
        else
            local answer = GK.Run(PREFIX .. "return " .. watch.code)
            watchValues[index] = GK.CutUtf8(GK.Format(answer), 400)
        end
    end
end

local function showWatches()
    if not ui then return end
    for index, row in ipairs(ui.watchRows) do
        local text = watchValues[index] or "..."
        if row.shown ~= text then row.value.Label = text row.shown = text end
    end
end

--- Pins an expression: it is evaluated once a second on `side` ("client" or "server") while
--- the window is open (and whenever GK.ConsoleWatches() is called).
function GK.ConsoleWatch(code, side)
    side = side == "server" and "server" or "client"
    S.watches[#S.watches + 1] = { side = side, code = tostring(code) }
    watchGeneration = watchGeneration + 1
    watchValues = {}
    touch()
    rebuildWatches()
    evaluateWatches()
    return #S.watches
end

function GK.ConsoleUnwatch(index)
    if index == nil then S.watches = {} else table.remove(S.watches, index) end
    watchGeneration = watchGeneration + 1
    watchValues = {}
    touch()
    rebuildWatches()
    evaluateWatches()
    return #S.watches
end

--- The pinned expressions with their latest values: { { side, code, value } }. Server values
--- arrive a moment after they are asked for.
function GK.ConsoleWatches()
    evaluateWatches()
    local out = {}
    for index, watch in ipairs(S.watches) do
        out[index] = { side = watch.side, code = watch.code, value = watchValues[index] }
    end
    return out
end

-- Actions

local actions = {}
local function builtin(group, label, target, code, tip)
    actions[#actions + 1] = { group = group, label = label, target = target, code = code, tip = tip }
end
builtin("Read", "Party", "server", "return GK.Party()", "Every party member: hp, position, equipment")
builtin("Read", "Host character", "server", "return GK.Character()")
builtin("Read", "Client state", "client", "return GK.Client()", "Game state, input mode, who is controlled")
builtin("Read", "Scenes", "client", "return GK.Scenes()", "Live dialogues and cutscenes")
builtin("Read", "Dialogue on screen", "ui", "return GK.Dialog()")
builtin("Read", "Game commands", "ui", "return GK.Commands()", "Names usable with GK.Command(name)")
builtin("Read", "View models", "ui", "return GK.ViewModels()")
builtin("Read", "Last server events", "server", "local e = GK.Events().events local o = {} for i = math.max(1, #e - 19), #e do o[#o + 1] = e[i] end return o")
builtin("Do", "Fast scenes x20", "client", "return GK.FastScenes(20)", "Dialogues and cutscenes play 20 times faster")
builtin("Do", "Scenes at normal speed", "client", "return GK.FastScenes(1)")
builtin("Do", "Autosaves off", "server", "return GK.AutoSave(false)")
builtin("Do", "Autosaves on", "server", "return GK.AutoSave(true)")
builtin("Do", "Draw weapons", "server", "return GK.Unsheath(true)")
builtin("Do", "Sheathe weapons", "server", "return GK.Unsheath(false)")

local function addActionItem(action)
    if not ui then return end
    local menu = ui.menus[action.group]
    if not menu then
        menu = ui.menuBar:AddMenu(action.group)
        ui.menus[action.group] = menu
    end
    local item = menu:AddItem(action.label)
    item.OnClick = function()
        if action.run then action.run() else GK.ConsoleRun(action.target, action.code) end
    end
    if action.tip then item:Tooltip():AddText(action.tip) end
end

--- Adds an item to the console's menu bar: `group` is the menu, `target` "client" / "server" /
--- "ui". For a mod's own test helpers.
function GK.ConsoleAction(group, label, target, code, tip)
    local action = { group = tostring(group), label = tostring(label), target = target or "client",
        code = tostring(code), tip = tip }
    actions[#actions + 1] = action
    addActionItem(action)
    return #actions
end

-- Saving

local function saveSettings()
    S.stamp = now()
    S.open = window ~= nil and window.Open or false
    writeJson(SETTINGS_FILE, S)
    settingsDirty = false
end

local function saveLog()
    local entries = {}
    for i = math.max(1, #log - 999), #log do entries[#entries + 1] = log[i] end
    writeJson(LOG_FILE, { stamp = now(), entries = entries })
end

--- Restarts Lua on both sides, as the Script Extender console's `reset` does. Files are read
--- again from where the game loads them; to pick up edits in a linked mod folder use
--- `gatekit.py reload` (it copies the files first).
function GK.ConsoleReload()
    saveSettings()
    saveLog()
    pcall(function() serverReset:SendToServer({}) end)
    local at = now() + 400
    local handler
    handler = Ext.Events.Tick:Subscribe(function()
        if now() < at then return end
        Ext.Events.Tick:Unsubscribe(handler)
        Ext.Debug.Reset()
    end)
    return true
end
actions[#actions + 1] = { group = "Do", label = "Reload Lua (both sides)", run = function() GK.ConsoleReload() end,
    tip = "Restarts Lua in place. For a linked mod folder use `gatekit.py reload`: it copies the files first." }

Ext.Events.Shutdown:Subscribe(function()
    pcall(saveSettings)
    pcall(saveLog)
end)

-- Events: the journal both sides keep (GK.Events), merged, on its own tab.

local EVENT_LINES, EVENTS_KEPT = 150, 600
local events = {}
local eventSince = { client = nil, server = nil }
local eventsDirty = true
local eventFilter = ""
local eventWaiting = false

local function stamp(ms)
    local seconds = ms / 1000
    return string.format("%3d:%04.1f", math.floor(seconds / 60), seconds % 60)
end

local function takeEvents(side, answer)
    if type(answer) ~= "table" then return end
    eventSince[side] = answer.last or eventSince[side]
    for _, event in ipairs(answer.events or {}) do
        local data = event.data
        if type(data) == "table" then
            local ok, text = pcall(Ext.Json.Stringify, data, { Beautify = false })
            data = ok and text or tostring(data)
        end
        events[#events + 1] = { side = side, n = event.n, t = event.t or 0, kind = tostring(event.kind),
            data = data ~= nil and tostring(data) or "" }
    end
    if #(answer.events or {}) > 0 then
        table.sort(events, function(a, b) return a.t < b.t end)
        while #events > EVENTS_KEPT do table.remove(events, 1) end
        eventsDirty = true
    end
end

-- At most the newest 60 per poll: a level load raises thousands of EnteredLevel at once.
local EVENTS_CODE = "local r = GK.Events(%s) local e, o = r.events, {} "
    .. "for i = math.max(1, #e - 59), #e do o[#o + 1] = e[i] end return { last = r.last, events = o }"

pollEvents = function()
    GK.ConsoleAsk("client", EVENTS_CODE:format(tostring(eventSince.client or 0)), function(ok, _, value)
        if ok then takeEvents("client", value) end
    end)
    if not eventWaiting then
        eventWaiting = true
        GK.ConsoleAsk("server", EVENTS_CODE:format(tostring(eventSince.server or 0)), function(ok, _, value)
            eventWaiting = false
            if ok then takeEvents("server", value) end
        end)
    end
    if not ui or not eventsDirty then return end
    eventsDirty = false
    local wanted = eventFilter:lower()
    local picked = {}
    for i = #events, 1, -1 do
        local event = events[i]
        if wanted == "" or (event.kind .. " " .. event.data):lower():find(wanted, 1, true) then
            picked[#picked + 1] = event
            if #picked >= EVENT_LINES then break end
        end
    end
    for i = 1, EVENT_LINES do
        local event = picked[#picked - i + 1]
        local line = ui.eventLines[i]
        if event then
            line.Label = stamp(event.t) .. (event.side == "server" and " S " or " C ") .. event.kind .. "  " .. event.data
            line:SetColor("Text", COLORS[event.side])
            line.Visible = true
        else
            line.Visible = false
        end
    end
    ui.eventChild:SetScroll({ 0, 10000000 })
end

buildEventsTab = function(tabs)
    local tab = tabs:AddTabItem("Events")
    ui.tabs.Events = tab
    local filter = tab:AddInputText("##dk_efilter", eventFilter)
    filter.ItemWidth = -1
    filter.Hint = "What happened in the game, both sides (GK.Events). Type to filter: Equipped, Dialog, GameState..."
    filter.OnChange = function()
        eventFilter = filter.Text
        eventsDirty = true
    end
    ui.eventChild = tab:AddChildWindow("dk_events")
    ui.eventChild.Border = true
    ui.eventLines = {}
    for i = 1, EVENT_LINES do
        local line = ui.eventChild:AddText("")
        line.TextWrapPos = 0
        line.Visible = false
        ui.eventLines[i] = line
    end
    eventsDirty = true
end

--- The event journal of both sides, merged by time: { { side, n, t, kind, data } }, the newest
--- `limit` (default 100) whose kind or data contains `text`. Fresh server events arrive a
--- moment after the first call.
function GK.ConsoleEvents(opts)
    opts = opts or {}
    pollEvents()
    local wanted = (opts.text or ""):lower()
    local out = {}
    for i = #events, 1, -1 do
        local event = events[i]
        if (not opts.side or event.side == opts.side)
            and (wanted == "" or (event.kind .. " " .. event.data):lower():find(wanted, 1, true)) then
            table.insert(out, 1, event)
            if #out >= (opts.limit or 100) then break end
        end
    end
    return out
end

-- Building

-- Colours a widget's text from the log palette and remembers it for the next theme change.
tint = function(widget, key)
    widget:SetColor("Text", COLORS[key] or COLORS.client)
    if ui then ui.tinted[#ui.tinted + 1] = { widget = widget, key = key } end
    return widget
end

local function checkbox(parent, label, checked, sameLine, onChange, tip)
    local box = parent:AddCheckbox(label, checked)
    box.SameLine = sameLine
    box.OnChange = function() onChange(box.Checked) end
    if tip then box:Tooltip():AddText(tip) end
    return box
end

local function button(parent, label, sameLine, onClick, tip)
    local b = parent:AddButton(label)
    b.SameLine = sameLine
    b.OnClick = onClick
    if tip then b:Tooltip():AddText(tip) end
    return b
end

build = function()
    window = Ext.IMGUI.NewWindow("GateKit Console")
    window.Closeable = true
    window.Open = false
    requestedSize = S.size or { 980, 620 }
    window:SetSize(requestedSize, "Always")
    local viewport = Ext.IMGUI.GetViewportSize()
    local pos = S.pos
    if pos and pos[1] >= 0 and pos[2] >= 0 and pos[1] < viewport[1] - 80 and pos[2] < viewport[2] - 40 then
        window:SetPos(pos, "Always")
    else
        window:SetPos({ 40, 40 }, "Always")
    end
    window.OnClose = function() touch() end
    Weave.Skin(window, "window", "console")     -- the theme's look; follows theme changes by itself
    if Overlay.Get("font") ~= "" then window.Font = Overlay.Get("font") fontSet = true end

    ui = { lines = {}, lineCache = {}, suggest = {}, menus = {}, watchRows = {}, tinted = {} }
    ui.menuBar = window:AddMainMenu()
    for _, action in ipairs(actions) do addActionItem(action) end

    local tabs = window:AddTabBar("dk_tabs")

    -- Log tab
    ui.tabs = {}
    ui.tabBar = tabs
    local tab = tabs:AddTabItem("Console")
    ui.tabs.Console = tab
    ui.tabs.Log = tab
    ui.sideBoxes = {
        client = checkbox(tab, "Client", S.sides.client, false, function(on) S.sides.client = on touch() logDirty = true end),
        server = checkbox(tab, "Server", S.sides.server, true, function(on) S.sides.server = on touch() logDirty = true end),
    }
    tab:AddText(" | ").SameLine = true
    ui.levelBoxes = {}
    for _, level in ipairs(LEVELS) do
        local box = checkbox(tab, level, S.levels[level], true, function(on) S.levels[level] = on touch() logDirty = true end)
        tint(box, level)
        ui.levelBoxes[level] = box
    end
    tab:AddText(" | ").SameLine = true
    ui.source = tab:AddCombo("##dk_source")
    ui.source.SameLine = true
    ui.source.ItemWidth = 190
    ui.source.OnChange = function()
        local name = ui.sourceOptions[ui.source.SelectedIndex + 1]
        S.source = (name and ui.source.SelectedIndex > 0) and name or ""
        touch()
        logDirty = true
    end
    ui.source:Tooltip():AddText("One mod, or one [tag] that lines start with")
    ui.filter = tab:AddInputText("##dk_filter", S.filter)
    ui.filter.SameLine = true
    ui.filter.ItemWidth = -1
    ui.filter.Hint = "filter: words to find, -word to hide"
    ui.filter.OnChange = function()
        S.filter = ui.filter.Text
        touch()
        logDirty = true
    end

    ui.followBox = checkbox(tab, "Follow", follow, false, function(on) setFollow(on) end,
        "On: the view stays at the newest line. Off: it stands still and new lines wait.")
    ui.selectBox = checkbox(tab, "Select", view == "select", true, function(on) setView(on and "select" or "color") end,
        "A plain-text snapshot of the filtered log: select with the mouse, Ctrl+C copies.")
    button(tab, "Copy all", true, function() copyAll() end, can.clipboard
        and "Copies the whole filtered log to the clipboard"
        or "Puts the whole filtered log into the Select view, ready for Ctrl+A, Ctrl+C")
    button(tab, "Clear", true, clearLog)
    ui.status = tab:AddText("")
    ui.status.SameLine = true
    tint(ui.status, "Debug")

    ui.child = tab:AddChildWindow("dk_log")
    ui.child.Border = true
    can.scroll = pcall(function() return ui.child.LastScrollMax end)
    for i = 1, MAX_DRAWN do
        local line = ui.child:AddText("")
        line.TextWrapPos = 0
        line.Visible = false
        ui.lines[i] = line
        ui.lineCache[i] = { visible = false }
    end
    ui.select = tab:AddInputText("##dk_select", "")
    ui.select.Multiline = true
    ui.select.ReadOnly = true
    ui.select.Visible = false

    for i = 1, SUGGESTIONS do
        local b = tab:AddButton("##dk_s" .. i)
        b.SameLine = i > 1
        b.Visible = false
        b.OnClick = function()
            local match = completion.matches[i]
            if match then
                local new = completed(inputText(), match)
                setInput(new, true)
                if not S.editor and not can.cursor then sticky, stickySelected = new, true end
            end
        end
        ui.suggest[i] = b
    end
    ui.suggestMore = tab:AddText("")
    tint(ui.suggestMore, "Debug")

    ui.target = tab:AddCombo("##dk_target")
    ui.target.Options = TARGETS
    ui.target.ItemWidth = 90
    for i, name in ipairs(TARGETS) do if name == S.target then ui.target.SelectedIndex = i - 1 end end
    ui.target.OnChange = function()
        S.target = TARGETS[ui.target.SelectedIndex + 1] or "client"
        touch()
        requestCompletion(inputText())
    end
    ui.target:Tooltip():AddText("Where the code runs. ui: the client inside the game UI's own context (Ext.UI, GK.Dialog, GK.Command)")

    ui.input = tab:AddInputText("##dk_input", "")
    ui.input.SameLine = true
    ui.input.ItemWidth = -330
    ui.input.EnterReturnsTrue = true
    ui.input.Hint = "Lua: return GK.Party()"
    ui.input.OnChange = function() submit(ui.input.Text) end
    -- The GateKit's Script Extender build hands over Up / Down and Tab and knows the cursor;
    -- on the stock build these assignments raise an error, and Tab is read as a character.
    can.history = pcall(function() ui.input.OnHistory = function(_, direction) historyStep(direction, true) end end)
    can.completion = pcall(function() ui.input.OnCompletion = function(_, cursor) tabAt(cursor) end end)
    can.cursor = pcall(function() return ui.input.CursorPos + 0 end)
    ui.input.AllowTabInput = not can.completion

    ui.editor = tab:AddInputText("##dk_editor", "")
    ui.editor.SameLine = true
    ui.editor.Multiline = true
    ui.editor.AllowTabInput = true
    ui.editor.SizeHint = { -330, 150 }
    ui.editor.Visible = false

    button(tab, "Run", true, function() submit() end)
    button(tab, "<", true, function() historyStep(-1) end, "Previous command")
    button(tab, ">", true, function() historyStep(1) end, "Next command")
    ui.historyPopup = tab:AddPopup("dk_history")
    button(tab, "History", true, function()
        for _, child in ipairs(ui.historyPopup.Children) do child:Destroy() end
        if #S.history == 0 then ui.historyPopup:AddText("(nothing yet)") end
        for i = #S.history, math.max(1, #S.history - 29), -1 do
            local code = S.history[i]
            local label = code:gsub("%s+", " ")
            if #label > 110 then label = GK.CutUtf8(label, 110) .. "..." end
            local item = ui.historyPopup:AddSelectable(label .. "##dk_h" .. i)
            item.OnClick = function() setInput(code, true) end
        end
        ui.historyPopup:Open()
    end, "The last 30 commands; kept between runs")
    button(tab, "Pin", true, function()
        local code = inputText():gsub("\t", ""):match("^%s*(.-)%s*$")
        if code ~= "" then GK.ConsoleWatch(code:gsub("^return%s+", ""), S.target == "server" and "server" or "client") end
    end, "Adds the expression to the Watch tab")
    ui.editorBox = checkbox(tab, "Editor", S.editor, true, function(on)
        local text = inputText()
        S.editor = on
        touch()
        layout()
        setInput(text, true)
        showSuggestions()
    end, "A multi-line box: Enter adds a line, the Run button executes")

    -- Watch tab
    buildEventsTab(tabs)

    tab = tabs:AddTabItem("Watch")
    ui.tabs.Watch = tab
    tab:AddText("Expressions evaluated once a second while this window is open.")
    ui.watchSide = tab:AddCombo("##dk_wside")
    ui.watchSide.Options = { "client", "server" }
    ui.watchSide.SelectedIndex = 0
    ui.watchSide.ItemWidth = 90
    ui.watchInput = tab:AddInputText("##dk_winput", "")
    ui.watchInput.SameLine = true
    ui.watchInput.ItemWidth = -160
    ui.watchInput.EnterReturnsTrue = true
    ui.watchInput.Hint = "GK.Character().hp"
    local function addWatch()
        local code = ui.watchInput.Text:match("^%s*(.-)%s*$")
        if code == "" then return end
        GK.ConsoleWatch(code, ui.watchSide.SelectedIndex == 1 and "server" or "client")
        ui.watchInput.Text = ""
    end
    ui.watchInput.OnChange = addWatch
    button(tab, "Add", true, addWatch)
    button(tab, "Remove all", true, function() GK.ConsoleUnwatch() end)
    tab:AddSeparator()
    ui.watchEmpty = tab:AddText("Nothing pinned. The Pin button beside the input line adds what is typed there.")
    tint(ui.watchEmpty, "Debug")
    ui.watchList = tab:AddGroup("dk_watches")

    -- Settings tab
    tab = tabs:AddTabItem("Settings")
    ui.tabs.Settings = tab
    ui.keyText = tab:AddText("")
    button(tab, "Overlay settings...", true, function() GK.OverlaySettings(true) end,
        "Theme, text size, the key, the button under the minimap, what the game still receives: for every GateKit window")
    showOverlay()
    button(tab, "Reset window size and position", false, function()
        window:SetSize({ 980, 620 }, "Always")
        window:SetPos({ 40, 40 }, "Always")
        requestedSize, scale = { 980, 620 }, nil
    end)
    tab:AddSeparator()
    local notes = tab:AddText(table.concat({
        "The log shows what Lua prints on both sides: print, _P, _D, Ext.Utils.Print*, Ext.Log.*, per mod.",
        can.consoleMessages
            and "This Script Extender build also hands over its own lines (a Lua error inside another mod's event"
            or "It cannot show what Script Extender itself writes (a Lua error inside another mod's event",
        can.consoleMessages
            and "handler, Osiris errors): they are in the log as source \"Script Extender\" / \"Osiris\"."
            or "handler, Osiris errors): for those see the Script Extender console or `gatekit.py logs`.",
        "",
        "Files, under %LOCALAPPDATA%\\Larian Studios\\Baldur's Gate 3\\Script Extender\\" .. DIR:gsub("/", "\\") .. ":",
        "  console.json            filters, history, watches, the window's place",
        "  overlay.json            the key, the HUD button (Overlay settings)",
        "  ..\\Gwynerva_Weave\\settings.json   theme, text size, input blocking (Weave's, shared by every mod)",
        "  console_transcript.log  every command and its result",
        "  console_copy.txt        what `Copy all` copied last",
        "",
        "From code: GK.ConsoleLog{...}, GK.ConsoleState(), GK.ConsoleSet{...}, GK.ConsoleRun(target, code).",
    }, "\n"))
    tint(notes, "Debug")

    layout()
    refreshSources()
    rebuildWatches()
    showSuggestions()
    setView(view)
    for _, hook in ipairs(GK.ConsoleHooks.built) do
        local ok, problem = pcall(hook, window, tabs, ui.tabs)
        if not ok then GK.ConsoleEmit("Error", "Console", "a panel failed to build: " .. tostring(problem)) end
    end
end

-- The overlay's text size, applied to this window. ("" is Script Extender's own size, Medium;
-- a font once set cannot be unset, only replaced.)
setFont = function(name)
    if not window then return end
    if name ~= "" then window.Font = name fontSet = true elseif fontSet then window.Font = "Medium" end
    -- the lines change height a frame later: scroll to the end again once they have
    layout()
    rescrollAt = now() + 300
    scrollSeen, scrollHold = nil, now() + 600
end

showOverlay = function()
    if ui then ui.keyText.Label = "Open / close key: " .. Overlay.Get("key") .. "     Theme: " .. Overlay.Get("theme") end
end

Overlay.OnChange(function(key, value)
    if key == "font" then setFont(value) end
    showOverlay()
end)

-- After a theme change: the palette is read again and everything coloured from it repainted.
Weave.OnRestyle(function()
    loadColors()
    if not ui then return end
    for i = #ui.tinted, 1, -1 do
        local item = ui.tinted[i]
        if not pcall(function() item.widget:SetColor("Text", COLORS[item.key] or COLORS.client) end) then table.remove(ui.tinted, i) end
    end
    for _, cache in ipairs(ui.lineCache) do cache.color = nil end
    logDirty = true
end)

local function open(on)
    if not window then build() end
    if window.Open == on then return on end
    window.Open = on
    if on then
        logDirty = true
        window:SetFocus()
        field():Activate()
    end
    touch()
    return on
end

--- Opens or closes the console (what the key does). Returns whether it is open now.
function GK.ToggleConsole()
    if not window then build() end
    return open(not window.Open)
end

-- --- Per frame ----------------------------------------------------------------------------

local nextRefresh, nextSecond, nextSave = 0, 0, 0

Ext.Events.Tick:Subscribe(function()
    local t = now()
    if reopen and t > 1500 then
        reopen = false
        pcall(open, true)
    end
    if settingsDirty and t >= nextSave then
        nextSave = t + 2000
        saveSettings()
    end
    if not window or not window.Open then return end

    -- The input line: Tab arrives as a character; everything else is only watched for changes.
    local text = inputText()
    if text ~= inputSeen then
        if not S.editor then
            if text:find("\t", 1, true) then
                local atEnd = text:sub(-1) == "\t"
                local clean = text:gsub("\t", "")
                local new = atEnd and completed(clean) or clean
                setInput(new, false)
                sticky, stickySelected = new ~= clean and new or nil, false
                text = new
            elseif sticky then
                if #text > #sticky and text:sub(-#sticky) == sticky then
                    -- typed in front of the completed text: it belongs behind it
                    text = sticky .. text:sub(1, #text - #sticky)
                    setInput(text, false)
                    sticky = text
                elseif stickySelected and #text <= 4 and not text:find(sticky, 1, true) then
                    -- typed over the completed text while it was all selected
                    text = sticky .. text
                    setInput(text, false)
                    sticky = text
                else
                    sticky = nil
                end
                stickySelected = false
            end
        end
        inputSeen = text
        historyAt = historyAt and S.history[historyAt] == text and historyAt or nil
        requestCompletion(text)
    end

    watchScroll()
    if rescrollAt and t >= rescrollAt then rescrollAt = nil logDirty = true end
    if logDirty and t >= nextRefresh then
        nextRefresh = t + REFRESH_MS
        refresh()
    end

    if t >= nextSecond then
        nextSecond = t + 1000
        if #S.watches > 0 then
            evaluateWatches()
            showWatches()
        end
        pollEvents()
        for _, hook in ipairs(GK.ConsoleHooks.second) do pcall(hook) end
        -- A window that slid out of reach (the game window got smaller) comes back by itself.
        Weave.KeepOnScreen(window)
        -- Window size and position, in the units SetSize takes (the UI scale divided out).
        local size, pos = window.LastSize, window.LastPosition
        if size and size[1] > 0 then
            if not scale then scale = size[1] / requestedSize[1] end
            local w, h = math.floor(size[1] / scale + 0.5), math.floor(size[2] / scale + 0.5)
            local x, y = math.floor(pos[1] + 0.5), math.floor(pos[2] + 0.5)
            if not S.size or S.size[1] ~= w or S.size[2] ~= h or not S.pos or S.pos[1] ~= x or S.pos[2] ~= y then
                S.size, S.pos = { w, h }, { x, y }
                touch()
            end
        end
    end
end)

-- --- Keys and mouse -----------------------------------------------------------------------
-- The key, Esc and what the game still receives while the window is open are the overlay's
-- business (Client/Overlay.lua); the console only says when it is open and how to close it.

Overlay.Register({
    isOpen = function() return window ~= nil and window.Open end,
    close = function() open(false) end,
    reset = function()      -- GK.OverlayRescue: back to the default place, and shown
        if not window then build() end
        window:SetSize({ 980, 620 }, "Always")
        window:SetPos({ 40, 40 }, "Always")
        requestedSize, scale = { 980, 620 }, nil
        open(true)
    end,
})

Ext.RegisterConsoleCommand("gk", function() GK.ToggleConsole() end)

-- --- The console as an API ----------------------------------------------------------------

--- Log entries as data. `opts` (all optional):
---   since   only entries numbered above this (use the `last` of the previous answer)
---   side    "client" | "server"
---   level   the lowest level wanted: "Debug" | "Info" | "Warning" | "Error"
---   source  a mod name, or "[tag]"
---   text    words to find, "-word" to exclude (as in the window's filter box)
---   kind    "cmd" | "result" (console commands and their results only)
---   ticket  what GK.ConsoleRun returned: that command's echo and result
---   limit   at most this many, the newest (default 200)
--- Returns { last, total, entries = { { n, side, level, source, tag, text, count, kind, ticket, clock } } }.
function GK.ConsoleLog(opts)
    opts = opts or {}
    local f = { source = opts.source, filter = opts.text, kind = opts.kind, ticket = opts.ticket }
    if opts.side then f.sides = { [opts.side] = true } end
    if opts.level then
        f.levels = {}
        for _, level in ipairs(LEVELS) do f.levels[level] = RANK[level] >= (RANK[opts.level] or 1) end
    end
    local entries = select(matcher(f), opts.limit or 200, nil, opts.since)
    local out = {}
    for i, entry in ipairs(entries) do
        out[i] = { n = entry.n, side = entry.side, level = entry.level, source = entry.source, tag = entry.tag,
            text = entry.text, count = entry.count or 1, kind = entry.kind, ticket = entry.ticket,
            target = entry.target, clock = entry.clock }
    end
    return { last = counter, total = #log, entries = out }
end

--- The log as the window shows it now (its filters applied), as text.
function GK.ConsoleOutput()
    local entries = select(matcher(filters()), MAX_DRAWN, not follow and frozenAt or nil)
    local lines = {}
    for i, entry in ipairs(entries) do lines[i] = lineOf(entry) end
    return table.concat(lines, "\n")
end

--- Everything about the window: is it open, what is typed, the filters, the counters.
function GK.ConsoleState()
    local sourceCounts = {}
    for _, name in ipairs(sourceList) do sourceCounts[name] = sources[name] end
    local active = false
    if ui then
        pcall(function() active = tostring(field().StatusFlags):find("Active", 1, true) ~= nil end)
    end
    local suggestions = {}
    for i, match in ipairs(completion.matches) do suggestions[i] = match end
    local cursor, scroll
    if ui and can.cursor then pcall(function() cursor = field().CursorPos end) end
    if ui and can.scroll then
        pcall(function() scroll = { y = ui.child.LastScroll[2], max = ui.child.LastScrollMax[2] } end)
    end
    local features = {}
    for name, value in pairs(can) do features[name] = value and true or false end
    return {
        features = features,      -- what this Script Extender build adds (docs/se-fork.md)
        cursor = cursor,          -- byte offset in the input line; nil on the stock build
        scroll = scroll,          -- { y, max } of the log view, pixels; nil on the stock build
        hint = completion.hint,
        open = window ~= nil and window.Open or false,
        target = S.target,
        editor = S.editor,
        input = inputText(),
        inputActive = active,
        follow = follow,
        view = view,
        filters = { sides = S.sides, levels = S.levels, source = S.source, text = S.filter },
        counts = counts,
        sources = sourceCounts,
        total = #log,
        last = counter,
        shown = shownCount,
        waiting = follow and 0 or (counter - frozenAt),
        suggestions = suggestions,
        history = #S.history,
        watches = #S.watches,
        key = Overlay.Get("key"),
        font = Overlay.Get("font"),
        theme = Overlay.Get("theme"),
        blockKeys = Overlay.Get("blockKeys"),
        blockMouse = Overlay.Get("blockMouse"),
        size = S.size,
        pos = S.pos,
    }
end

--- Changes the window as a user would. Any of: open, target, editor, input, cursor (with
--- input: where the cursor goes, GateKit Script Extender build only), scroll ("top" | "end" |
--- pixels: scrolls the log as the wheel would), focus (true:
--- put the cursor into the input line), follow, view ("color" | "select"), sides =
--- { client, server }, levels = { Debug, Info, Warning, Error }, source, text, key,
--- blockKeys, blockMouse, font ("Tiny" .. "Big"), tab ("Log" | "Watch" | "Settings"). Returns GK.ConsoleState().
function GK.ConsoleSet(opts)
    if not window then build() end
    if opts.open ~= nil then open(opts.open and true or false) end
    if opts.target then
        for i, name in ipairs(TARGETS) do
            if name == opts.target then S.target = name ui.target.SelectedIndex = i - 1 end
        end
    end
    if opts.editor ~= nil then
        local text = inputText()
        S.editor = opts.editor and true or false
        layout()
        setInput(text, false)
        showSuggestions()
    end
    if opts.input then setInput(tostring(opts.input), false, opts.cursor) requestCompletion(tostring(opts.input)) end
    if opts.sides then
        for side, on in pairs(opts.sides) do
            if S.sides[side] ~= nil then S.sides[side] = on and true or false ui.sideBoxes[side].Checked = S.sides[side] end
        end
    end
    if opts.levels then
        for level, on in pairs(opts.levels) do
            if S.levels[level] ~= nil then S.levels[level] = on and true or false ui.levelBoxes[level].Checked = S.levels[level] end
        end
    end
    if opts.source then S.source = tostring(opts.source) refreshSources() end
    if opts.text then S.filter = tostring(opts.text) ui.filter.Text = S.filter end
    if opts.follow ~= nil then setFollow(opts.follow) end
    if opts.view then setView(opts.view == "select" and "select" or "color") end
    -- these four are the overlay's now (GK.OverlaySet); kept here so older calls go on working
    if opts.key ~= nil or opts.font ~= nil or opts.blockKeys ~= nil or opts.blockMouse ~= nil or opts.theme ~= nil then
        GK.OverlaySet({ key = opts.key, font = opts.font, blockKeys = opts.blockKeys, blockMouse = opts.blockMouse,
            theme = opts.theme })
    end
    if opts.focus then field():Activate() end
    if opts.tab and ui.tabs[opts.tab] then
        -- the flag means "select this tab now"; left on, it would pin the tab
        local tab = ui.tabs[opts.tab]
        tab.SetSelected = true
        local at = now() + 250
        local handler
        handler = Ext.Events.Tick:Subscribe(function()
            if now() < at then return end
            Ext.Events.Tick:Unsubscribe(handler)
            tab.SetSelected = false
        end)
    end
    touch()
    logDirty = true
    if window.Open then refresh() end
    if opts.scroll ~= nil then      -- what the mouse wheel does: "top", "end" or pixels from the top
        local y = opts.scroll == "top" and 0 or (opts.scroll == "end" and 10000000 or tonumber(opts.scroll) or 0)
        ui.child:SetScroll({ 0, y })
    end
    return GK.ConsoleState()
end

--- Runs what is in the input line, as Enter does. Returns the ticket, or nil for an empty line.
function GK.ConsoleSubmit()
    if not window then build() end
    return submit()
end

--- Completes the input line as Tab does and returns the new text.
function GK.ConsoleTab()
    if not window then build() end
    local text = inputText()
    if completion.text ~= text then requestCompletion(text) end
    local new = completed(text)
    setInput(new, false)
    return new
end

--- What the Copy all button does: the filtered log as text, to the clipboard where this
--- Script Extender build has one, otherwise into the Select view; always into console_copy.txt.
--- Returns { lines, bytes, clipboard = <did it reach the clipboard>, file }.
function GK.ConsoleCopy()
    if not window then build() end
    return copyAll()
end

function GK.ConsoleHistory()
    local out = {}
    for i, code in ipairs(S.history) do out[i] = code end
    return out
end

function GK.ConsoleClear()
    clearLog()
    return true
end
