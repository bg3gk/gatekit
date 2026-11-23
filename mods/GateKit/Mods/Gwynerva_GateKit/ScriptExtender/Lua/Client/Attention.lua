-- GK.Attention, client side: the windows that wait for the player, a film, the game's state.
-- What it sees becomes rows of the event log (Shared/Attention.lua); this file only looks. One look per tick at the names of
-- the open widgets (the children of ContentRoot: a few dozen reads), whose changes are also the
-- `ui_page` / `ui_page_ended` rows of every page of the interface, and the registry's full
-- check of the blocking windows (GK.Gui.BlockingWindows) when that list changed, else at most
-- every RECHECK ticks (a reaction prompt shows inside a widget that is always there). Every
-- interval and limit here is counted in the client's ticks (GK.LimitTicks).

local A = GK.Attention
local UI = GK.Gui
if not (A and UI) then return false end

local RECHECK = GK.LimitTicks(200)
local FILM = GK.LimitTicks(500)

-- What a window of the registry is to an agent: a kind of the event log. A blocking window the
-- list does not name is a `window` row with its registry name in `window`.
local WHAT = { dialogue = "dialogue", roll = "roll", reaction = "reaction", box = "box", reward = "reward", trade = "trade",
               loot = "loot", pickpocket = "loot", rest = "rest", tutorial = "tutorial", sidetutorial = "tutorial",
               learn = "learn", split = "split", book = "book", gamemenu = "menu", save = "save", load = "load",
               savename = "save", Eula = "setup", ModBrowserSplashScreen = "setup", SensitiveSettings = "setup",
               HDRCalibration = "setup", GammaCalibration = "setup", SplashScreen = "splash" }
local function whatOf(window) return WHAT[window] or "window" end

local look = A.Look or { pending = false, names = "", fullAt = 0, film = 0 }
look.closed = look.closed or {}
look.final = look.final or {}

look.opening = look.opening or {}

-- Windows whose numbers the rows carry: read every tick while open. The window shows before its
-- view model is filled, so the opening row waits for the first reading that is `ready` (at most
-- READY ticks) and takes its OPEN fields; the closing row takes the CLOSE fields of the last reading
-- (the view model is gone once the window has closed).
local READY = GK.LimitTicks(4000)
local FOLLOW = {
    roll = {
        read = function(widget) return UI.Windows.roll.read(UI.Data(widget), UI) end,
        -- ready when the window waits for the throw: its introduction is over (WaitForStart, or
        -- any later state when the introduction was skipped). The roller (SourceObject) alone
        -- does not say that the list of modifiers is filled.
        ready = function(fields) return fields.who ~= nil and fields.state ~= "IntroductionAnimation" and fields.state ~= "?" end,
        open = { "who", "check", "skill", "pure", "context", "dc", "target", "advantage", "modifiers", "boosts", "bonus",
                 "inspiration" },
        close = { "who", "check", "skill", "pure", "dc", "advantage", "modifiers", "bonus", "added", "dice", "natural", "total",
                  "success", "critical", "state", "inspiration", "rerolls" },
        -- a throw again with inspiration: the window goes back to WaitForReRoll
        again = function(before, now) return now.state == "WaitForReRoll" and before.state ~= "WaitForReRoll" end,
    },
}

-- A window of the registry marked `look` (the first-launch pages, the splash screen) opens its row
-- with what a player can act on: its `read`'s controls, or its keys when it has no control. The
-- page shows before its controls are made: Noesis makes and arranges them in layout passes, the
-- page first, then a list's items, then what an item holds (a combo box), one pass a frame. The row
-- waits, as FOLLOW's do, for the reading in which the layout has arranged the page and every control
-- it shows (`laidOut`: each one's ActualWidth stored, GK.Gui.Look's `layout`); it is read only until then.
local lookFollows = {}
local function lookFollow(window)
    local entry = UI.Windows[window]
    if not (entry and entry.look and entry.read) then return nil end
    lookFollows[window] = lookFollows[window] or {
        open = { "controls", "keys", "firstLaunch" }, close = {}, look = true,
        read = function(widget) return entry.read(UI.Data(widget), UI) end,
        ready = function(fields) return fields.keys ~= nil or fields.laidOut == true end,
    }
    return lookFollows[window]
end

local function pick(fields, names)
    if type(fields) ~= "table" then return nil end
    local out = {}
    for _, name in ipairs(names) do out[name] = fields[name] end
    return out
end

local function readFollowed(follow, widget)
    local ok, fields = pcall(follow.read, widget)
    if ok and type(fields) == "table" and not fields.error then return fields end
    return nil
end

-- Opens a followed window's waiting state with the reading's OPEN fields.
local function openFollowed(key, pending, fields)
    local item = pending.item
    for k, v in pairs(pick(fields, pending.follow.open) or {}) do item[k] = v end
    look.opening[key] = nil
    A.Set(key, item)
end
local CLOSED = GK.LimitTicks(500)
A.Look = look

local now = GK.Ticks

-- The pages of the interface: the widgets under ContentRoot, each by its x:Name, or by its view
-- model for a widget that has none ("gui::DCHDRCalibration" -> "HDRCalibration"), as GK.Gui names
-- them. Returns their names as one text and { page, widget, element } in tree order.
local function openNames()
    local ok, root = pcall(function() return Ext.UI.GetRoot():Find("ContentRoot") end)
    if not ok or not root then return "", {} end
    local n = 0
    pcall(function() n = root.VisualChildrenCount end)
    local names, pages = {}, {}
    for i = 1, n do
        local element, name
        pcall(function() element = root:VisualChild(i) end)
        pcall(function() name = element.Name end)
        local page = name
        if page == nil or page == "" then
            name = nil
            local data
            pcall(function() data = tostring(element.DataContext.Type) end)
            page = data and (data:match("::DC(%w+)$") or data:match("%.DC(%w+)$") or data) or "?"
        end
        names[#names + 1] = page
        pages[#pages + 1] = { page = page, widget = name, element = element }
    end
    return table.concat(names, ","), pages
end

-- The pages entered and left since the last look, as rows of the log (`ui_page`,
-- `ui_page_ended`): a state of the interface read each tick, told when it changes. A page shown
-- twice (two widgets of one name) is two rows.
look.pages = look.pages or {}
local function pageRows(pages)
    local log = GK.EventLog
    local count, first = {}, {}
    for _, p in ipairs(pages) do
        count[p.page] = (count[p.page] or 0) + 1
        first[p.page] = first[p.page] or p
    end
    local before = look.pages
    for page, was in pairs(before) do
        for _ = (count[page] or 0) + 1, #was do
            local gone = table.remove(was)
            pcall(log.Append, "ui_page_ended", { page = page, widget = gone.widget, viewModel = gone.viewModel })
        end
        if #was == 0 then before[page] = nil end
    end
    for _, p in ipairs(pages) do
        local open = before[p.page] or {}
        if #open < count[p.page] then
            local viewModel
            pcall(function() viewModel = tostring(p.element.DataContext.Type) end)
            open[#open + 1] = { widget = p.widget, viewModel = viewModel }
            before[p.page] = open
            pcall(log.Append, "ui_page", { page = p.page, widget = p.widget, viewModel = viewModel })
        end
    end
end

local function scan()
    local started = Ext.Timer.MicrosecTime()
    local names, pages = openNames()
    local t = now()
    if names ~= look.names then pageRows(pages) end
    if names ~= look.names or t - look.fullAt >= RECHECK then
        look.names, look.fullAt = names, t
        local ok, found = pcall(UI.BlockingWindows)
        if ok and type(found) == "table" then
            local seen = {}
            for _, w in ipairs(found) do
                local key = "window:" .. w.window
                seen[key] = true
                local follow = FOLLOW[w.window] or lookFollow(w.window)
                -- a window a hook has just seen close may show a frame longer: not opened again
                local justClosed = look.closed[key] and t - look.closed[key] < CLOSED
                if not justClosed and not (A.Waiting[key] and A.Waiting[key].hooked) then
                    local item = { what = whatOf(w.window), window = w.window, widget = w.widget,
                                   choices = UI.BlockingChoices and UI.BlockingChoices[w.window] or nil }
                    if w.window == "box" then
                        local data = UI.Data(w.widget)
                        local over = false
                        pcall(function() over = data.IsGameOver end)
                        if over then item.what = "gameover"; item.gameOver = true end
                    end
                    if follow and not A.Waiting[key] then
                        -- read below, every tick, until it is ready
                        look.opening[key] = look.opening[key] or { item = item, follow = follow, widget = w.widget, since = t }
                    elseif not follow then
                        A.Set(key, item)
                    end
                end
            end
            for key, pending in pairs(look.opening) do
                -- closed before it was ready: opened with what was read, and closed below; a page
                -- gone before its layout arranged it was never drawn: no row
                if not seen[key] then
                    if pending.follow.look then
                        look.opening[key], look.final[key] = nil, nil
                    else
                        openFollowed(key, pending, look.final[key])
                    end
                end
            end
            for key, item in pairs(A.Waiting) do
                -- a hooked state ends with its hook, not with what the scan happens to see
                if key:sub(1, 7) == "window:" and not seen[key] and not item.hooked then
                    local follow = FOLLOW[key:sub(8)]
                    A.Set(key, nil, follow and pick(look.final[key], follow.close) or nil)
                    look.final[key] = nil
                end
            end
        end
    end
    -- a followed window is read every tick: the opening row waits for a ready reading, the
    -- closing row has the last numbers
    for key, pending in pairs(look.opening) do
        local fields = readFollowed(pending.follow, pending.widget)
        if fields then look.final[key] = fields end
        if (fields and pending.follow.ready(fields)) or t - pending.since >= READY then openFollowed(key, pending, fields or look.final[key]) end
    end
    for window, follow in pairs(FOLLOW) do
        local key = "window:" .. window
        local open = A.Waiting[key]
        if open and open.widget then
            local fields = readFollowed(follow, open.widget)
            if fields then
                local before = look.final[key]
                local rerolls = (before and before.rerolls) or 0
                if before and follow.again and follow.again(before, fields) then rerolls = rerolls + 1 end
                fields.rerolls = rerolls > 0 and rerolls or nil
                look.final[key] = fields
            end
        end
    end
    -- where an open dialogue stands, every tick: a choice (or a "continue") is a new item the
    -- moment its answers show, and again for every new set of answers; a line being spoken is
    -- only a waiting entry (`waiting = false` for an agent: nothing to answer yet)
    if A.Waiting["window:dialogue"] and UI.DialoguePhase then
        local ok, p = pcall(UI.DialoguePhase)
        if ok and p then
            if p.phase == "line" then
                A.Set("dialogue:phase", { what = "dialogue_line", window = "dialogue", set = p.set, speaker = p.speaker,
                    text = p.text, ready = false })
            else
                A.Set("dialogue:phase", { what = "dialogue_" .. p.phase, window = "dialogue", set = p.set, speaker = p.speaker,
                    text = p.text, answers = p.answers, canTrade = p.canTrade, ready = true })
            end
        end
    elseif A.Waiting["dialogue:phase"] then
        A.Set("dialogue:phase", nil)
    end
    local spent = (Ext.Timer.MicrosecTime() - started) / 1000
    local cost = A.Cost
    cost.uiLooks = (cost.uiLooks or 0) + 1
    cost.uiMs = (cost.uiMs or 0) + spent
    if spent > (cost.uiMax or 0) then cost.uiMax = spent end
end

A.Pollers.windows = function()
    if look.pending or not (Ext.UI and Ext.UI.Defer) then return end
    look.pending = true
    Ext.UI.Defer(function()
        look.pending = false
        pcall(scan)
    end)
end

local descentPoll   -- the main menu's camera descent, below: looked at every tick from here

A.Pollers.film = function()
    if descentPoll then descentPoll() end
    local t = now()
    if t - look.film < FILM or not UI.FilmPlaying then return end
    look.film = t
    local playing = UI.FilmPlaying()
    A.Set("film", playing == true and { what = "film" } or nil)
end

-- Hooks on the engine's own components: a state that begins and ends with a component is recorded
-- the moment the component is created and destroyed - in the same tick, from the engine's event -
-- so it is kept even when it is over before the next look (a game running ten times faster
-- answers a prompt or closes a window between two ticks of the scan). The reaction prompt is the
-- case: ReactionDecisionPopup stays in the tree; ecl::interrupt::PlayerDecisionComponent exists
-- only while the player is asked (seen 2026-11-14: 1 during the prompt, 0 after it).
A.Hooks = A.Hooks or {
    { component = "ClientInterruptPlayerDecision", key = "window:reaction",
      item = { what = "reaction", window = "reaction", widget = "ReactionDecisionPopup" } },
}
A.HookCount = A.HookCount or {}
A.HookSubscribed = A.HookSubscribed or {}

function A.OnHook(component, created)
    for _, hook in ipairs(A.Hooks) do
        if hook.component == component then
            local n = math.max(0, (A.HookCount[component] or 0) + (created and 1 or -1))
            A.HookCount[component] = n
            if created and n == 1 then
                local item = { hooked = true, choices = UI.BlockingChoices and UI.BlockingChoices[hook.item.window] or nil }
                for k, v in pairs(hook.item) do item[k] = v end
                A.Set(hook.key, item)
            elseif not created and n == 0 and A.Waiting[hook.key] then
                A.Set(hook.key, nil)
                look.closed[hook.key] = now()
            end
        end
    end
end

for _, hook in ipairs(A.Hooks) do
    local c = hook.component
    if not A.HookSubscribed[c] and Ext.Entity.OnCreate then
        A.HookSubscribed[c] = pcall(function()
            Ext.Entity.OnCreate(c, function() pcall(GK.Attention.OnHook, c, true) end)
            Ext.Entity.OnDestroy(c, function() pcall(GK.Attention.OnHook, c, false) end)
        end)
    end
end

-- Loads, saves and the main menu (key `state`), from the client's state machine: the client
-- passes every load, the server does not exist at the start. Measured with
-- Ext.StartupVideos.Timeline() (research/studies/startup-videos.md):
--   the start      InitMenu, StartLoading, Init, LoadModule (the first Lua), LoadMenu, StopLoading, Menu
--   a save, a new game from the menu
--                  Menu, StartLoading, StartServer, Idle, InitNetwork, InitConnection, StartLoading,
--                  Idle, UnloadLevel, UnloadSession, Idle (a new Lua state), LoadSession, LoadLevel,
--                  SwapLevel, StopLoading, PrepareRunning, Running
--   a save in a session
--                  Running, StartLoading, Idle, UnloadLevel, UnloadSession, Idle (a new Lua state),
--                  LoadSession, ... PrepareRunning, Running
--   to the menu    Running, StartLoading, Disconnect, UnloadLevel, UnloadSession, LoadMenu (a new
--                  Lua state), StopLoading, Menu
-- A load begins when the client enters StartLoading (or any state of a load with none open); it
-- ends when the loading screen is hidden: the client enters Menu or PrepareRunning (the
-- LoadingScreenHidden row of the timeline is in that tick; PrepareRunning lasts about a second, or
-- as long as a new game's opening film), or Running. Every state between is inside the load: no
-- row of its own here (the `game_state` rows have them). Save is `saving`; Menu is `menu`.
local L = GK.EventLog
local LOAD_ENDS = { Menu = true, PrepareRunning = true, Running = true }
local QUIET = { Paused = true, Unknown = true }

-- What a client state means under the key `state`: a kind, or nil.
local function meaning(state)
    if state == "Save" then return "saving" end
    if state == "Menu" then return "menu" end
    if LOAD_ENDS[state] or QUIET[state] then return nil end
    return "loading"
end

local function clientState()
    local ok, state = pcall(function() return tostring(Ext.Utils.GetGameState()) end)
    return ok and state or nil
end

-- The load the timeline shows the client in now: the first StartLoading after the last state
-- that ends a load, read after the fact (Ext.StartupVideos.Timeline(), GateKit's Script
-- Extender build). { startedMs, from } or nil.
local function timelineLoad()
    if not (Ext.StartupVideos and Ext.StartupVideos.Timeline) then return nil end
    local ok, rows = pcall(Ext.StartupVideos.Timeline)
    if not ok or type(rows) ~= "table" then return nil end
    local began
    for _, e in ipairs(rows) do
        if e.Side == "Client" and e.What == "State" then
            local to = tostring(e.To)
            if LOAD_ENDS[to] or to == "Save" then began = nil
            elseif not began and to == "StartLoading" then began = { startedMs = e.Ms, from = tostring(e.From) } end
        end
    end
    return began
end

-- The `state` row the previous Lua state of this client left open, in this game process.
local function previousOpen()
    local ok, rows = pcall(L.Previous)
    if not ok or type(rows) ~= "table" then return nil end
    for i = #rows, 1, -1 do
        local row = rows[i]
        if row.key == "state" then
            if row.attention == "begin" or row.attention == "hold" then return row end
            return nil
        end
    end
    return nil
end

A.Client = A.Client or {}
local client = A.Client

--- At a boot of the client's Lua (the start, a load that resets Lua, a `reset`): what the state
--- machine is in now is opened again. A state the previous Lua state of this process already told
--- carries `continues` = { boot, seq } of that row, so that a reader tells it once
--- (bridge-protocol.md, "Attention"); one it left open that is over now is opened and ended here,
--- so that its end is told. A load with no previous row (the start: no Lua at StartLoading) carries
--- the timeline's `startedMs` and `from`.
-- (local, not names of GK.Attention: every public function of the bridge has a class in
-- Shared/VerbClasses.json, which this work could not edit)
local function clientBoot()
    if client.booted then return end
    client.booted = true
    local state = clientState()
    if not state then return end
    client.state = state
    local wanted = meaning(state)
    local prev = previousOpen()
    local continues = prev and L.PreviousHead and { boot = L.PreviousHead.boot, seq = prev.seq } or nil
    if wanted then
        local item = { what = wanted, state = state }
        if prev and prev.kind == wanted then
            local p = prev.payload or {}
            item.from, item.startedMs, item.continues = p.from, p.startedMs, continues
        elseif wanted == "loading" then
            local began = timelineLoad()
            if began then item.from, item.startedMs = began.from, began.startedMs end
        end
        if prev and prev.kind ~= wanted then
            local p = prev.payload or {}
            A.Set("state", { what = prev.kind, state = p.state, from = p.from, startedMs = p.startedMs, continues = continues })
            A.Set("state", nil, { to = state })
        end
        A.Set("state", item)
    elseif prev then
        local p = prev.payload or {}
        A.Set("state", { what = prev.kind, state = p.state, from = p.from, startedMs = p.startedMs, continues = continues })
        A.Set("state", nil, { to = state })
    end
end

--- A change of the client's state machine (Ext.Events.GameStateChanged).
local function onClientState(from, to)
    clientBoot()
    from, to = tostring(from), tostring(to)
    client.state = to
    local open = L.Open.state
    local kind = open and open.kind or nil
    local wanted = meaning(to)
    if wanted == nil and QUIET[to] then return end
    if kind == wanted then return end                    -- a state inside the same load, menu or save
    if kind then A.Set("state", nil, { to = to }) end
    if wanted then A.Set("state", { what = wanted, state = to, from = from }) end
end

if not client.subscribed then
    client.subscribed = true
    Ext.Events.GameStateChanged:Subscribe(function(e)
        local ok, problem = pcall(onClientState, e.FromState, e.ToState)
        if not ok then Ext.Utils.PrintWarning("[GateKit] attention, the client's state: " .. tostring(problem)) end
    end)
end
do
    local ok, problem = pcall(clientBoot)
    if not ok then Ext.Utils.PrintWarning("[GateKit] attention, the client's state at boot: " .. tostring(problem)) end
end

-- The main menu's camera descent (key `menu:descent`): the timelines the menu's accept event (the
-- "press any key" page's key) starts, whichever they are. The menu level's cinematic arena
-- triggers bind timelines to the menu's events; when one fires, the menu's timeline system marks
-- its trigger played (ecl::timeline::PlayedMainMenuTriggerComponent) with one entity per timeline
-- it lists (ecl::timeline::MainMenuActiveEventComponent, Event = the timeline's resource). The
-- open event's trigger is played while the menu loads, so the first trigger played after the
-- client enters Menu is the accept's. The descent begins when one of its timelines plays
-- (TimelineData.PlaybackState 2) and ends when the first one that played is back at rest - the
-- camera's move, where a trigger also lists a longer timeline without a camera
-- (research/studies/startup-videos.md, stages 15 and 16). TimelineData has no replication event
-- (Ext.Entity.OnChange refuses it), so the accept's timelines are read every tick, only in Menu;
-- the triggers and the timelines' entities are looked up again at most every DESCENT_LOOK.
local DESCENT_LOOK = GK.LimitTicks(500)
client.descent = client.descent or { lookedAt = -math.huge }

--- The played menu triggers: { [trigger] = { timeline resource, ... } in the order the events were made }.
local function playedTriggers()
    local out = {}
    local ok, list = pcall(Ext.Entity.GetAllEntitiesWithComponent, "ClientTimelinePlayedMainMenuTrigger")
    for _, trigger in ipairs(ok and list or {}) do
        local events = {}
        pcall(function()
            for k, v in pairs(trigger.ClientTimelinePlayedMainMenuTrigger.Events) do
                local e = type(k) == "userdata" and k or v
                events[#events + 1] = { key = tostring(e), timeline = tostring(e.ClientTimelineMainMenuActiveEvent.Event) }
            end
        end)
        if #events > 0 then
            table.sort(events, function(a, b) return a.key < b.key end)
            local timelines = {}
            for i, e in ipairs(events) do timelines[i] = e.timeline end
            out[tostring(trigger)] = timelines
        end
    end
    return out
end

local function menuDescent()
    local d = client.descent
    if client.state ~= "Menu" then
        d.before, d.accept, d.entities, d.played, d.done = nil, nil, nil, nil, nil
        if A.Waiting["menu:descent"] then A.Set("menu:descent", nil, { to = client.state }) end
        return
    end
    if d.done then return end
    local t = now()
    if not d.before then
        d.before, d.lookedAt = playedTriggers(), t
        return
    end
    if not d.accept then
        if t - d.lookedAt < DESCENT_LOOK then return end
        d.lookedAt = t
        for trigger, timelines in pairs(playedTriggers()) do
            if not d.before[trigger] then d.accept, d.entities, d.played, d.lookedAt = timelines, {}, {}, -math.huge break end
        end
        if not d.accept then return end
    end
    local missing = false
    for _, id in ipairs(d.accept) do
        if not d.entities[id] then missing = true end
    end
    if missing and t - d.lookedAt >= DESCENT_LOOK then
        d.lookedAt = t
        local ok, list = pcall(Ext.Entity.GetAllEntitiesWithComponent, "TimelineData")
        for _, e in ipairs(ok and list or {}) do
            pcall(function()
                local id = tostring(e.ClientTimelineLoaded.Timeline)
                for _, wanted in ipairs(d.accept) do if id == wanted then d.entities[id] = e end end
            end)
        end
    end
    local open = A.Waiting["menu:descent"]
    for _, id in ipairs(d.accept) do
        local e = d.entities[id]
        if e then
            local ok, playback = pcall(function() return e.TimelineData.PlaybackState end)
            if not ok then
                d.entities[id] = nil
            elseif playback == 2 then
                d.played[id] = true
                if not open then
                    A.Set("menu:descent", { what = "menu_descent", timeline = id, playback = playback })
                    open = true
                end
            elseif d.played[id] and open then
                A.Set("menu:descent", nil, { timeline = id, playback = playback })
                d.done = true
                return
            end
        end
    end
end
descentPoll = menuDescent

return true
