-- AI (client side): what only the client knows, told to the server, and the acts that are the
-- player's own clicks, carried out in the UI's context. mods/AI/README.md.
--
-- To the server, on the mod's channel GKAI:
--   { open, blocking }   the open waiting states of GateKit's attention (a dialogue with its answers,
--                        a roll window, a tutorial, a reaction ...) and the top window that blocks
--                        with its text, whenever one opens or closes and with every act's answer
--   { scenes, stuck }    the state of each table under the client's GKScene, and what GK.Gui.Stuck
--                        says holds the game (a film), when either changed
--   { id, answer }       the end of an act the server asked for
--   { hello }            the answer to the server's hello: the client's channel is up
--   { tool, args, forward }   a tool call, answered by the server (Client/Tool.lua)
-- From the server: { id, act, ... } - "resolve" (GK.Gui.Resolve with a policy), "cast", "attack"
-- (GK.Act), "ui" (GK.Gui.Look, Click, Set of a window), "event" (a widget's own input event),
-- "key" (GK.Key), "run" (a chunk of the stand in GateKit's environment, `ui`: in the UI's context),
-- "call" (a call of a scene under the client's GKScene, in the UI's context),
-- "autosave" (GK.AutoSave), "reset" (the newest save loaded from the game-over screen); each
-- followed to what GateKit observed. { hello } every second while the server runs; { reply, text }
-- the answer to a forwarded call.

local GK = AI.GK
local L = GK.EventLog
local channel
local pending = {}       -- acts being followed: { id, act, step, before, looks, tries }
local busy = false
-- seconds of the game's time a dialogue answer is shown in violet before it is pressed (0: never)
AI.MarkSeconds = AI.MarkSeconds or 1

local function short(text, n)
    if type(text) ~= "string" then return text end
    text = text:gsub("%s+", " ")
    if #text > n then
        local last = n - 3
        while last > 0 and text:byte(last + 1) and text:byte(last + 1) >= 0x80 and text:byte(last + 1) < 0xC0 do last = last - 1 end
        return text:sub(1, last) .. "..."
    end
    return text
end

local FIELDS = { "window", "widget", "speaker", "text", "check", "skill", "dc", "who", "ready", "canTrade" }

local function answersOf(list)
    local out = {}
    for i, a in ipairs(list or {}) do
        if i > 8 then break end
        out[i] = { n = type(a) == "table" and tonumber(a.n) or i, text = short(type(a) == "table" and (a.text or a.Text) or tostring(a), 160) }
    end
    return out
end

local function openStates()
    local list = {}
    for key, row in pairs(L.Open) do
        local p = row.payload or {}
        local item = { key = key, kind = row.kind, seq = row.seq }
        for _, f in ipairs(FIELDS) do
            local v = p[f]
            if type(v) ~= "table" then item[f] = short(v, 400) end
        end
        if type(p.answers) == "table" then item.answers = answersOf(p.answers) end
        list[#list + 1] = item
    end
    return list
end

local function signature()
    local parts = {}
    for key, row in pairs(L.Open) do parts[#parts + 1] = tostring(key) .. ":" .. tostring(row.seq) end
    table.sort(parts)
    return table.concat(parts, ",")
end

-- The top window that blocks, in the UI's context: { window, text, answers }.
local function blocking()
    local ok, b = pcall(GK.Gui.Blocking)
    if not ok or type(b) ~= "table" or not b.window then return nil end
    local s = type(b.summary) == "table" and b.summary or {}
    local text = s.text or s.title or s.message or s.description
    -- a reaction prompt's choices: who reacts and with what (GK.Gui reaction reader), and 0 for none
    local options
    if type(s.options) == "table" and #s.options > 0 then
        options = {}
        for _, o in ipairs(s.options) do
            options[#options + 1] = { n = o.n, text = tostring(o.who or "?") .. ": " .. tostring(o.name or "?") }
        end
        options[#options + 1] = { n = 0, text = "do not react" }
    end
    return { window = b.window, policy = b.policy or b.window, text = type(text) == "string" and short(text, 400) or nil,
             answers = (type(s.answers) == "table" and answersOf(s.answers)) or (type(s.buttons) == "table" and answersOf(s.buttons))
                 or (options and answersOf(options)) or nil }
end

local function send(message)
    if not channel then return end
    message.open, message.blocking = openStates(), blocking()
    pcall(function() channel:SendToServer(message) end)
end

local function defer(fn)
    if Ext.UI and Ext.UI.Defer then Ext.UI.Defer(fn) else fn() end
end

--- The mod's listener on every row of the client's log: a row that opens or closes a waiting
--- state sends the open states to the server.
L.Listeners = L.Listeners or {}
L.Listeners.ai = function(row)
    if row.attention and row.attention ~= "news" then defer(function() send({}) end) end
end

-- What an act's answer says, in a few words: the scalars GateKit answered, and a summary.
local function brief(final)
    if type(final) ~= "table" then return { ok = true } end
    local out = {}
    for k, v in pairs(final) do
        local kind = type(v)
        if kind == "boolean" or kind == "number" then out[k] = v elseif kind == "string" then out[k] = (k == "answered" or k == "examined") and v or short(v, 200) end
    end
    local words = {}
    if final.success ~= nil and final.result ~= nil and type(final.result) ~= "table" then
        words[#words + 1] = "rolled " .. tostring(final.result) .. (final.dc and (" against " .. tostring(final.dc)) or "")
            .. (final.success ~= nil and (final.success and ", success" or ", failure") or "")
    end
    if type(final.outcomes) == "table" then
        for i, o in ipairs(final.outcomes) do
            if i > 4 then break end
            words[#words + 1] = tostring(o.outcome)
        end
    end
    if #words > 0 then out.summary = table.concat(words, ", ") end
    if type(final.window) == "table" then out.window = final.window end
    if out.ok == nil then out.ok = true end
    return out
end

local LOOK_LINES = 40

-- A window's controls as lines: handle, kind, text, value, a list's first items. `find` keeps the
-- controls whose handle or text holds it (GK.Gui.Look's own option).
local function lookLines(window, find)
    local look = GK.Gui.Look(window, { find = find, items = 8 })
    if type(look) ~= "table" or look.ok == false then
        return { ok = false, error = type(look) == "table" and tostring(look.error or "the window could not be read") or "the window could not be read" }
    end
    local lines = {}
    local controls = look.controls or {}
    for _, c in ipairs(controls) do
        if #lines >= LOOK_LINES then break end
        local text = tostring(c.handle) .. " " .. tostring(c.kind)
        if type(c.text) == "string" and c.text ~= "" then text = text .. ' "' .. short(c.text, 40) .. '"' end
        -- what a repeated control stands for (its view model's text: an ability's row, a spell)
        if type(c.of) == "string" and c.of ~= "" and c.of ~= c.text then
            text = text .. " of " .. short(c.of, 40) .. (type(c.ofValue) == "number" and (" " .. tostring(c.ofValue)) or "")
        end
        if c.value ~= nil and type(c.value) ~= "table" then text = text .. " = " .. short(tostring(c.value), 40) end
        if c.enabled == false then text = text .. " (disabled)" end
        if c.kind == "list" and type(c.items) == "table" then
            local items = {}
            for i, it in ipairs(c.items) do
                if i > 8 then break end
                items[#items + 1] = tostring(it.n or i) .. " " .. short(tostring(it.text or it.name or it.id or "?"), 24)
            end
            local total = tonumber(c.count) or #c.items
            text = text .. ": " .. table.concat(items, ", ") .. (total > #items and (" (of " .. total .. ")") or "")
        end
        lines[#lines + 1] = text
    end
    if #controls > #lines then
        lines[#lines + 1] = string.format("%d more: value=<a part of a handle or text> finds them", #controls - #lines)
    end
    return { ok = true, window = lines, did = "looked at " .. window .. (find and (" for " .. find) or "") }
end

-- A value of the ui act: a number when it reads as one (a slider, a list's item number), else the text.
local function valueOf(text)
    if type(text) == "string" and text:match("^%s*%-?%d+%.?%d*%s*$") then return tonumber(text) end
    return text
end

-- What a reset waits for: { hold, seen, tries, until } - the newest save loaded from the game-over
-- screen once it has stood `hold` seconds.
local resetting

local function begin(entry)
    local act = entry.act
    local result
    if act.act == "ui" then
        local window, control = act.window, act.control
        if type(window) ~= "string" or window == "" then
            result = { ok = false, error = "ui needs window: the name of an open window (now.waiting names it)" }
        elseif not GK.Gui.Widget(window) then
            result = { ok = false, error = "no window " .. window .. " is open" }
        elseif type(control) ~= "string" or control == "" then
            result = lookLines(window, act.value ~= "" and act.value or nil)
        else
            -- what the window shows before the press: a press that turns its page answers with
            -- the new page's controls, so the next press needs no look of its own
            local ok, shown = pcall(GK.Gui.Shown, GK.Gui.Widget(window))
            entry.shownBefore = ok and shown or nil
            entry.openBefore = {}
            for _, name in ipairs(AI.OpenWindows()) do entry.openBefore[name] = true end
            if act.value ~= nil and act.value ~= "" then
                result = GK.Gui.Set(window, control, valueOf(act.value))
            else
                result = GK.Gui.Click(window, control)
            end
        end
    elseif act.act == "event" then
        result = GK.Gui.Event(act.event, act.widget)
    elseif act.act == "key" then
        local ok, problem = pcall(GK.Key, act.key)
        result = { ok = ok, error = not ok and tostring(problem) or nil, did = "pressed " .. tostring(act.key) }
    elseif act.act == "run" then
        result = AI.Stand.Run(act.code)
    elseif act.act == "call" then
        result = AI.SceneCall(act.scene, act.call)
    elseif act.act == "autosave" then
        result = { ok = GK.AutoSave(act.on) and true or false }
    elseif act.act == "reset" then
        resetting = { hold = tonumber(act.hold) or 4, tries = 0, ends = Ext.Utils.MonotonicTime() + 90000 }
        result = { ok = true, did = "the newest save is loaded once the game-over screen has stood " .. resetting.hold .. " s" }
    elseif act.act == "resolve" then
        local b = blocking()
        if not b then
            result = { ok = false, error = "nothing waits for an answer" }
        elseif act.never and act.never[b.policy] then
            result = { ok = false, error = "a " .. b.policy .. " cannot be closed: answer it (do=answer, do=continue or do=roll)" }
        elseif act.only and not act.only[b.policy] then
            result = { ok = false, error = "that does not answer what waits: a " .. b.policy .. " window" }
        else
            -- the answer chosen in a dialogue is shown first: its text in the theme's violet for
            -- AI.MarkSeconds of the game's time (GK.Cheat.Mark), then pressed
            local n = type(act.policy) == "table" and act.policy.dialogue
            local mark = b.policy == "dialogue" and type(n) == "number" and AI.MarkSeconds > 0
                and GK.Cheat.Mark("Dialogue", "answerList#" .. n .. "/LSButton", { seconds = AI.MarkSeconds }) or nil
            if mark and mark.ok then
                local pressed
                entry.step = function()
                    if pressed then return pressed() end
                    if not mark.done() then return nil end
                    pressed = GK.Follow(GK.Gui.Resolve(act.policy))
                    return pressed()
                end
                entry.tries = (entry.tries or 0) + 1
                return
            end
            result = GK.Gui.Resolve(act.policy)
        end
    elseif act.act == "examine" then
        result = AI.Examine(act.target)
    elseif act.act == "cast" then
        result = GK.Act.Cast(act.spell, act.target)
    elseif act.act == "attack" then
        result = GK.Act.Attack(act.target)
    else
        result = { ok = false, error = "the client knows no act " .. tostring(act.act) }
    end
    entry.step = GK.Follow(result)
    entry.tries = (entry.tries or 0) + 1
end

-- One look at an act: its observation, then (an answer given) the waiting states having changed.
local function look(entry)
    if not entry.final then
        local ok, value = pcall(entry.step)
        if not ok then value = { ok = false, error = "the client's act failed: " .. tostring(value) } end
        if value == nil then return false end
        -- a roll window is thrown, then gone on from: the same step again while it stands
        -- (a window still in its opening animation takes no throw: until a result is read, the
        -- throw is tried again every ten looks, up to twelve times)
        local again = entry.act.again
        if again and type(value) == "table" and value.ok ~= false and entry.tries < 12 then
            local b = blocking()
            if b and b.policy == again then
                entry.kept = entry.kept or value
                if value.result ~= nil then entry.kept = value end
                entry.pause = (entry.pause or 0) + 1
                if entry.kept.result == nil and entry.pause < 10 then return false end
                entry.pause = 0
                begin(entry)
                return false
            end
        end
        entry.final = entry.kept and entry.kept.result ~= nil and entry.kept or value
        entry.looks = 0
    end
    entry.looks = entry.looks + 1
    local failed = type(entry.final) == "table" and entry.final.ok == false
    if entry.act.act == "resolve" and not failed and signature() == entry.before and entry.looks < 90 then return false end
    -- a press that opened a window, or turned the pressed window's page: that window's controls
    -- come with the answer, so the next press needs no look of its own
    if type(entry.shownBefore) == "string" and not failed and type(entry.final) == "table" and type(entry.final.window) ~= "table" then
        local opened
        for _, name in ipairs(AI.OpenWindows()) do
            if entry.openBefore and not entry.openBefore[name] then opened = opened and false or name end
        end
        local page
        if opened then
            page = lookLines(opened)
            if page.ok and #page.window > 0 then
                table.insert(page.window, 1, "the window " .. opened .. " opened; its controls:")
            elseif (entry.waitBuilt or 0) < 30 then
                -- a window that has just opened builds its controls a few frames later
                entry.waitBuilt = (entry.waitBuilt or 0) + 1
                return false
            end
        else
            local widget = GK.Gui.Widget(entry.act.window)
            local ok, shown = pcall(GK.Gui.Shown, widget)
            if widget and ok and type(shown) == "string" and shown ~= entry.shownBefore then
                -- the old page slides away for ~0.6 s and stays Visible meanwhile: the page is read
                -- once what is shown has held still for twenty looks
                if shown ~= entry.lastShown then entry.lastShown, entry.steady = shown, 0 end
                entry.steady = (entry.steady or 0) + 1
                if entry.steady < 20 and (entry.waitSteady or 0) < 90 then
                    entry.waitSteady = (entry.waitSteady or 0) + 1
                    return false
                end
                page = lookLines(entry.act.window)
                if page.ok and #page.window > 0 then
                    table.insert(page.window, 1, "the page changed; its controls now:")
                elseif (entry.waitOpen or 0) < 30 then
                    -- the pressed window shows nothing now: the window it opens comes a few frames later
                    entry.waitOpen = (entry.waitOpen or 0) + 1
                    return false
                end
            elseif (entry.waitOpen or 0) < 15 then
                -- nothing changed yet: a page or a window may come a few frames after the press
                entry.waitOpen = (entry.waitOpen or 0) + 1
                return false
            end
        end
        if page and page.ok and #page.window > 1 then entry.final.window = page.window end
    end
    entry.deliver(brief(entry.final))
    return true
end

local function lookAll()
    busy = false
    local i = 1
    while i <= #pending do
        local ok, done = pcall(look, pending[i])
        if not ok then
            pending[i].deliver({ ok = false, error = "the client's act failed: " .. tostring(done) })
            done = true
        end
        if done then table.remove(pending, i) else i = i + 1 end
    end
end

--- Examines a creature as a player does with the T key or the context menu's "Examine": the game's
--- own ExamineCommand (any HUD widget's data) on the creature's view model the interface holds
--- (OverheadInfo's Data.Characters), then the window's visible text, its summary and its details
--- tab (GK.Gui.Text), then the window closed. Observed: { ok, summary, details, ac, resist }; ac and
--- resist are what the window's AC and Resistances rows show, read from the same view model.
function AI.Examine(uuid)
    local data = GK.Gui.Data("OverheadInfo")
    local list = data and data.Data and data.Data.Characters
    local vm
    for i = 1, (list and #list or 0) do
        local c = list[i]
        if c and c.EntityUUID == uuid then vm = c end
    end
    if not vm then return { ok = false, error = "the game's interface holds no creature " .. tostring(uuid) .. " to examine" } end
    if vm.IsExaminable == false then return { ok = false, error = "the game does not let this creature be examined" } end
    local command = GK.Gui.Data("CursorText") and GK.Gui.Data("CursorText").ExamineCommand
    if not command then return { ok = false, error = "no examine command in the interface now" } end
    if GK.Gui.IsOpen("Examine") then GK.Gui.Close("Examine") end
    command:Execute(vm.EntityHandle)
    local okAc, acValue = pcall(function() return vm.Stats.ArmorClass.Value end)
    local ac = okAc and tonumber(acValue) and math.floor(tonumber(acValue) + 0.5) or nil
    local resist = {}
    pcall(function()
        local rs = vm.Stats.Resistances
        for i = 1, #rs do
            local r = rs[i]
            if r.Full and r.Full ~= "None" then resist[#resist + 1] = tostring(r.DamageType) .. " " .. tostring(r.Full):lower() end
        end
    end)
    local stage, summary, detailsAt = "open", nil, 0
    return GK.Observe({ ok = true, waiting = "the examine window" }, function(ticks, final)
        if final then
            GK.Gui.Close("Examine")
            return { ok = false, error = "the examine window did not show in time" }
        end
        if stage == "open" then
            local t = GK.Gui.IsOpen("Examine") and GK.Gui.Text("Examine")
            if not (t and t.ok and #t.texts > 3) then return nil end
            summary = table.concat(t.texts, " | ")
            -- the details tab: the tab list's third item (its second is not shown)
            GK.Gui.Click("Examine", "CharacterInfoPanel/TabControl#3")
            stage, detailsAt = "details", ticks
            return nil
        end
        local t = GK.Gui.Text("Examine")
        local text = t.ok and table.concat(t.texts, " | ") or ""
        if text == summary and ticks - detailsAt < 30 then return nil end
        local details = text ~= summary and text or nil
        GK.Gui.Close("Examine")
        return { ok = true, examined = summary .. (details and (" || " .. details) or ""), ac = ac,
                 resist = #resist > 0 and table.concat(resist, ", ") or nil }
    end, 5000)
end

--- A call of a scene under the client's GKScene, run here: { ok, answered } or { ok = false, error }
--- (AI.SceneSaid reads what the call answered). A call that answers an observation (GK.Observe) is
--- followed until it ends, and what it ends in is read the same way.
function AI.SceneCall(name, call)
    local scenes = AI.SceneTable()
    local scene = type(scenes) == "table" and scenes[name] or nil
    if type(scene) ~= "table" then return { ok = false, error = "no scene of the client is called " .. tostring(name) } end
    if type(scene[call]) ~= "function" then
        return { ok = false, error = "the scene " .. tostring(name) .. " has no call " .. tostring(call) .. "; its calls: " .. table.concat(AI.SceneCalls(scene), ", ") }
    end
    local ran, result = pcall(scene[call])
    if not ran then return { ok = false, error = name .. "." .. call .. "() failed: " .. tostring(result) } end
    local function read(value)
        local ok, said, why = AI.SceneSaid(value)
        if not ok then return { ok = false, error = why } end
        return { ok = true, answered = said }
    end
    if type(result) ~= "table" or not result.__gk_observe then return read(result) end
    local step = GK.Follow(result)
    return GK.Observe({ ok = true, waiting = name .. "." .. call }, function(_, final)
        local value = step()
        if value == nil then
            if final then return { ok = false, error = name .. "." .. call .. "() did not end" } end
            return nil
        end
        return read(value)
    end, 30000)
end


--- Carries out `act` (as the server would ask for it) and calls `deliver(answer)` with its end.
--- A chunk of the client's own context runs at once; everything else in the UI's context.
function AI.Carry(act, deliver)
    local function start()
        local entry = { act = act, before = signature(), deliver = deliver }
        local ok, problem = pcall(begin, entry)
        if not ok then
            deliver({ ok = false, error = "the client's act failed: " .. tostring(problem) })
            return
        end
        pending[#pending + 1] = entry
    end
    if act.act == "run" and not act.ui then start() else defer(start) end
end

--- The names of the open windows that are not the HUD's (GK.Gui.Open), sorted. A window that is
--- an entry of GK.Gui.Windows (a dialogue, a roll, a tutorial, loot ...) is left out: the server
--- tells it as a waiting line of its own kind with the act that answers it, and this list, sent
--- once in thirty frames, would still name it for a moment after it has gone.
function AI.OpenWindows()
    local ok, open = pcall(GK.Gui.Open)
    local names, seen = {}, {}
    pcall(function()
        for _, entry in pairs(GK.Gui.Windows or {}) do
            if type(entry) == "table" and entry.widget then seen[entry.widget] = true end
        end
    end)
    for _, w in ipairs(ok and open or {}) do
        if not w.hud and not seen[w.name] then seen[w.name] = true; names[#names + 1] = tostring(w.name) end
    end
    table.sort(names)
    return names
end

--- Sends a message to the server; false when the channel is not up.
function AI.ToServer(message)
    if not channel then return false end
    return (pcall(function() channel:SendToServer(message) end))
end

AI.Replies = AI.Replies or {}

-- what the client last told of its scenes; told again to a server it had not heard (a new level)
local sceneSig, sceneTick = "", 0

local function received(msg)
    if type(msg) ~= "table" then return end
    if msg.hello then
        if not AI.ServerUp() then sceneSig = "" end
        AI.ServerSeen = Ext.Utils.MonotonicTime()
        pcall(function() channel:SendToServer({ hello = true }) end)
        return
    end
    if msg.reply then
        AI.Replies[msg.reply] = msg.text
        return
    end
    if not msg.act then return end
    AI.Carry(msg, function(answer) send({ id = msg.id, answer = answer }) end)
end

-- The reset: once the game-over screen has stood its seconds, the screen's own Load and then the
-- newest save (GK.Cheat.Recover), a try a second; the load restarts Lua and ends it.
local function resetLook()
    local now = Ext.Utils.MonotonicTime()
    if now > resetting.ends then resetting = nil return end
    if resetting.next and now < resetting.next then return end
    resetting.next = now + 1000
    local stuck = GK.Gui.Stuck()
    local loadOpen = GK.Gui.Widget("LoadGame") ~= nil
    if stuck ~= "gameover" and not loadOpen then return end
    resetting.seen = resetting.seen or now
    if now - resetting.seen < resetting.hold * 1000 or resetting.tries >= 8 then return end
    resetting.tries = resetting.tries + 1
    defer(function() pcall(GK.Cheat.Recover) end)
end

Ext.Events.Tick:Subscribe(function()
    if not channel then
        local ok, made = pcall(Ext.Net.CreateChannel, ModuleUUID, "GKAI", received)
        if ok then channel = made end
    end
    if #pending > 0 and not busy then
        busy = true
        defer(lookAll)
    end
    if resetting then pcall(resetLook) end
    -- the client's scenes and what holds the game: read once in thirty frames, sent when changed
    sceneTick = sceneTick + 1
    if sceneTick >= 30 and channel then
        sceneTick = 0
        local lines = AI.SceneLines(Mods.GateKit)
        local parts = {}
        for name, line in pairs(lines) do parts[#parts + 1] = name .. ":" .. line end
        table.sort(parts)
        local ok, stuck = pcall(GK.Gui.Stuck)
        stuck = ok and stuck or nil
        local windows = AI.OpenWindows()
        parts[#parts + 1] = "|stuck:" .. tostring(stuck) .. "|windows:" .. table.concat(windows, ",")
        local sig = table.concat(parts, "|")
        if sig ~= sceneSig then
            sceneSig = sig
            pcall(function() channel:SendToServer({ scenes = lines, stuck = stuck or false, windows = windows }) end)
        end
    end
end)
