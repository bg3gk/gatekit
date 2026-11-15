-- GK.Attention, client side: the windows that wait for the player, a film, the game's state.
-- What it sees becomes rows of the event log (Shared/Attention.lua); this file only looks. One look per tick at the names of
-- the open widgets (the children of ContentRoot: a few dozen reads), and the registry's full
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
               savename = "save" }
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

local function openNames()
    local ok, root = pcall(function() return Ext.UI.GetRoot():Find("ContentRoot") end)
    if not ok or not root then return "" end
    local n = 0
    pcall(function() n = root.VisualChildrenCount end)
    local names = {}
    for i = 1, n do
        local name
        pcall(function() name = root:VisualChild(i).Name end)
        names[#names + 1] = name or "?"
    end
    return table.concat(names, ",")
end

local function scan()
    local started = Ext.Timer.MicrosecTime()
    local names = openNames()
    local t = now()
    if names ~= look.names or t - look.fullAt >= RECHECK then
        look.names, look.fullAt = names, t
        local ok, found = pcall(UI.BlockingWindows)
        if ok and type(found) == "table" then
            local seen = {}
            for _, w in ipairs(found) do
                local key = "window:" .. w.window
                seen[key] = true
                local follow = FOLLOW[w.window]
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
                -- closed before it was ready: opened with what was read, and closed below
                if not seen[key] then openFollowed(key, pending, look.final[key]) end
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

A.Pollers.film = function()
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

return true
