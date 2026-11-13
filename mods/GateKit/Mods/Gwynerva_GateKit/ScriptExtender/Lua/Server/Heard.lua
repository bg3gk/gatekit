-- GK.Heard: what was said around the party - barks over characters' heads, the narrator's lines
-- (a rune slate read aloud, thoughts), and the lines of dialogues - as a log the agent can read.
-- Server side. The game shows these as text over heads or as subtitles; a player reads them.
--
--   GK.Heard.Since(n, options)   { last, lines = { { n, t, speaker, text, dialog, automated, d } } }
--                                lines numbered above n; options.all = also lines spoken out of
--                                the party's hearing (debugging)
--   GK.Heard.Start() / Stop()    the capture runs from the bridge's start; Stop pauses it
--
-- How (found 2026-11-13, game 4.1.1.7631656): every running dialogue, automated barks included,
-- is an instance in Ext.Utils.GetDialogManager().Dialogs. Its CurrentNode.UUID names the node
-- being spoken in the dialogue resource (instance.Dialogs[...].Nodes[uuid]); the node's
-- TaggedTexts[1].Lines[1].TagText is the line's text handle; node.Speaker is the speaker's index
-- in instance.Speakers (-666: the narrator). The instances are polled every 150 ms: a bark's
-- instance lives about half a second.
--
-- Fair hearing: a line is kept when its speaker is the narrator, a party member, or within
-- earshot of a party member (the member's Hearing stat, about 18 m; 30 m for the narrator's
-- object-bound lines is not needed - the narrator speaks to the party).

GK.Heard = GK.Heard or {}
local Heard = GK.Heard
Heard.Lines = Heard.Lines or {}
Heard.Next = Heard.Next or 1
Heard.Seen = Heard.Seen or {}   -- instance id -> node uuid last recorded
Heard.Keep = 300
Heard.Narrator = -666

local function try(fn, ...)
    local ok, a, b = pcall(fn, ...)
    if ok then return a, b end
    return nil
end

local function uuidOf(entity)
    return try(function() return entity.Uuid.EntityUuid end)
end

local function textOf(node)
    local tagged = try(function() return node.TaggedTexts end)
    if not tagged then return nil end
    for _, t in ipairs(tagged) do
        for _, line in ipairs(try(function() return t.Lines end) or {}) do
            local handle = try(function() return line.TagText.Handle.Handle end)
            local text = handle and try(Ext.Loca.GetTranslatedString, handle)
            if text and text ~= "" then return text end
        end
    end
    return nil
end

local function nodeOf(instance, uuid)
    for _, resource in pairs(try(function() return instance.Dialogs end) or {}) do
        local node = try(function() return resource.Nodes[uuid] end)
        if node then return node end
    end
    return nil
end

local function speakerOf(instance, index)
    if index == nil or index < 0 then return nil end
    local speaker = try(function() return instance.Speakers[index + 1] end)
    return speaker and uuidOf(speaker) or nil
end

-- Whether a party member could hear `speaker` (a uuid): distance within its Hearing.
local function withinEarshot(speaker)
    local sp = GK.See.Position(speaker)
    if not sp then return false, nil end
    local best
    for _, m in ipairs(GK.See.Members()) do
        if m == speaker then return true, 0 end
        local mp = GK.See.Position(m)
        local e = try(Ext.Entity.Get, m)
        local range = e and try(function() return e.Hearing.Hearing end) or 18
        if mp then
            local dx, dy, dz = sp[1] - mp[1], sp[2] - mp[2], sp[3] - mp[3]
            local d = math.sqrt(dx * dx + dy * dy + dz * dz)
            if not best or d < best then best = d end
            if d <= range then return true, d end
        end
    end
    return false, best
end

local function record(instanceId, instance)
    local current = try(function() return instance.CurrentNode.UUID end)
    if not current or current == "" or Heard.Seen[instanceId] == current then return end
    Heard.Seen[instanceId] = current
    local node = nodeOf(instance, current)
    if not node then return end
    local text = textOf(node)
    if not text then return end
    local index = try(function() return node.Speaker end)
    local speaker = speakerOf(instance, index)
    local name
    if index == Heard.Narrator then
        name = "Narrator"
    elseif speaker then
        name = GK.See.Name(speaker)
    end
    local heard, d = true, nil
    if speaker and index ~= Heard.Narrator then heard, d = withinEarshot(speaker) end
    -- a bark repeated on a loop ("Help us!" every few seconds) is logged once a minute
    Heard.Recent = Heard.Recent or {}
    local key = tostring(name) .. "|" .. text
    local nowT = Ext.Utils.MonotonicTime() / 1000
    if Heard.Recent[key] and nowT - Heard.Recent[key] < 60 then return end
    Heard.Recent[key] = nowT
    local n = Heard.Next
    Heard.Next = n + 1
    Heard.Lines[n] = {
        n = n, t = math.floor(Ext.Utils.MonotonicTime() / 100) / 10, speaker = name, speakerId = speaker,
        text = text, automated = try(function() return instance.AutomatedDialog end) or nil,
        d = d and math.floor(d * 10 + 0.5) / 10 or nil, heard = heard,
        dir = speaker and index ~= Heard.Narrator and GK.See.Position(speaker) and GK.See.Position(GK.See.Members()[1])
            and GK.See.Compass(GK.See.Position(GK.See.Members()[1]), GK.See.Position(speaker)) or nil,
        pos = speaker and index ~= Heard.Narrator and GK.See.Position(speaker) or nil,
        dialog = try(function() return Ext.Types.Serialize(instance).DialogResourceUUID end),
    }
    Heard.Lines[n - Heard.Keep] = nil
end

function Heard.Poll()
    local manager = try(Ext.Utils.GetDialogManager)
    local dialogs = manager and try(function() return manager.Dialogs end)
    if not dialogs then return end
    local alive = {}
    for id, instance in pairs(dialogs) do
        alive[id] = true
        pcall(record, id, instance)
    end
    for id in pairs(Heard.Seen) do
        if not alive[id] then Heard.Seen[id] = nil end
    end
end

function Heard.Since(n, options)
    options = options or {}
    n = n or 0
    local out = {}
    for i = math.max(n + 1, Heard.Next - Heard.Keep), Heard.Next - 1 do
        local line = Heard.Lines[i]
        if line and (line.heard or options.all) then out[#out + 1] = line end
    end
    return { last = Heard.Next - 1, lines = out }
end

function Heard.Start()
    if Heard.Timer then return true end
    Heard.Timer = Ext.Timer.WaitFor(150, function() Heard.Poll() end, 150)
    return true
end

function Heard.Stop()
    if Heard.Timer then
        Ext.Timer.Cancel(Heard.Timer)
        Heard.Timer = nil
    end
    return true
end

Heard.Start()
return Heard
