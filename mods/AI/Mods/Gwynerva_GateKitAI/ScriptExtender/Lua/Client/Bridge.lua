-- AI (client side): what only the client knows, told to the server, and the acts that are the
-- player's own clicks, carried out in the UI's context. mods/AI/README.md.
--
-- To the server, on the mod's channel GKAI:
--   { open, blocking }   the open waiting states of GateKit's attention (a dialogue with its answers,
--                        a roll window, a tutorial, a reaction ...) and the top window that blocks
--                        with its text, whenever one opens or closes and with every act's answer
--   { scenes }           the state of each table under the client's GKScene, when it changed
--   { id, answer }       the end of an act the server asked for
-- From the server: { id, act = "resolve" | "cast" | "attack", ... } - GK.Gui.Resolve with a policy,
-- GK.Act.Cast, GK.Act.Attack; each followed to what GateKit observed.

local GK = AI.GK
local L = GK.EventLog
local channel
local pending = {}       -- acts being followed: { id, act, step, before, looks, tries }
local busy = false

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

local FIELDS = { "window", "speaker", "text", "check", "skill", "dc", "who", "ready", "canTrade" }

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
    return { window = b.window, policy = b.policy or b.window, text = type(text) == "string" and short(text, 400) or nil,
             answers = type(s.answers) == "table" and answersOf(s.answers) or nil }
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
        if kind == "boolean" or kind == "number" then out[k] = v elseif kind == "string" then out[k] = short(v, 200) end
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
    if out.ok == nil then out.ok = true end
    return out
end

local function begin(entry)
    local act = entry.act
    local result
    if act.act == "resolve" then
        local b = blocking()
        if not b then
            result = { ok = false, error = "nothing waits for an answer" }
        elseif act.never and act.never[b.policy] then
            result = { ok = false, error = "a " .. b.policy .. " cannot be closed: answer it (do=answer, do=continue or do=roll)" }
        elseif act.only and not act.only[b.policy] then
            result = { ok = false, error = "that does not answer what waits: a " .. b.policy .. " window" }
        else
            result = GK.Gui.Resolve(act.policy)
        end
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
        local again = entry.act.again
        if again and type(value) == "table" and value.ok ~= false and entry.tries < 4 then
            local b = blocking()
            if b and b.policy == again then
                entry.kept = entry.kept or value
                if value.result ~= nil then entry.kept = value end
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
    send({ id = entry.act.id, answer = brief(entry.final) })
    return true
end

local function lookAll()
    busy = false
    local i = 1
    while i <= #pending do
        local ok, done = pcall(look, pending[i])
        if not ok then
            send({ id = pending[i].act.id, answer = { ok = false, error = "the client's act failed: " .. tostring(done) } })
            done = true
        end
        if done then table.remove(pending, i) else i = i + 1 end
    end
end

local sceneSig, sceneTick = "", 0

Ext.Events.Tick:Subscribe(function()
    if not channel then
        local ok, made = pcall(Ext.Net.CreateChannel, ModuleUUID, "GKAI", function(msg)
            if type(msg) ~= "table" or not msg.act then return end
            defer(function()
                local entry = { act = msg, before = signature() }
                local ok, problem = pcall(begin, entry)
                if not ok then
                    send({ id = msg.id, answer = { ok = false, error = "the client's act failed: " .. tostring(problem) } })
                    return
                end
                pending[#pending + 1] = entry
            end)
        end)
        if ok then channel = made end
    end
    if #pending > 0 and not busy then
        busy = true
        defer(lookAll)
    end
    -- the client's scenes: read once in thirty frames, sent when their state changed
    sceneTick = sceneTick + 1
    if sceneTick >= 30 and channel then
        sceneTick = 0
        local lines = AI.SceneLines(Mods.GateKit)
        local parts = {}
        for name, line in pairs(lines) do parts[#parts + 1] = name .. ":" .. line end
        table.sort(parts)
        local sig = table.concat(parts, "|")
        if sig ~= sceneSig then
            sceneSig = sig
            pcall(function() channel:SendToServer({ scenes = lines }) end)
        end
    end
end)
