-- AI (server side): the table of the mod, what every other file of it uses, and what the client
-- tells the server. mods/AI/README.md.
--
--   AI.GK                  GateKit's API (Mods.GateKit.GK): the bridge is built from its verbs only
--   AI.Util                small helpers: try, uuidOf, pointOf, nameOf, controlled, members, handle, cut
--   AI.Row(kind, payload)  a row of the mod's own family `ai` in GateKit's event log
--   AI.Client              what the client last told: { open, blocking, scenes, stuck, alive, seq }
--   AI.Ask(act, done)      an act carried out on the client, in the UI's context; `done(answer)`
--   AI.Class               each verb's class (Shared/VerbClasses.json)

local GK = AI.GK
local U = AI.Util or {}
AI.Util = U

function U.try(fn, ...)
    local ok, a, b, c = pcall(fn, ...)
    if ok then return a, b, c end
end
local try = U.try

function U.uuidOf(id)
    if type(id) ~= "string" then return nil end
    return id:match("(%x%x%x%x%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%x%x%x%x%x%x%x%x)$")
end

--- A point from { x, y, z }, { x = .. } or the text "x,y,z" (spaces allowed), else nil.
function U.pointOf(t)
    if type(t) == "string" then
        local x, y, z = t:match("^%s*%[?%s*(-?[%d%.]+)%s*[,; ]%s*(-?[%d%.]+)%s*[,; ]%s*(-?[%d%.]+)%s*%]?%s*$")
        if x then return { tonumber(x), tonumber(y), tonumber(z) } end
        return nil
    end
    if type(t) ~= "table" then return nil end
    local x, y, z = t.x or t[1], t.y or t[2], t.z or t[3]
    if type(x) == "number" and type(y) == "number" and type(z) == "number" then return { x, y, z } end
end

function U.nameOf(id)
    if type(id) ~= "string" then return nil end
    return try(GK.See.Name, id) or id
end

function U.members() return try(GK.See.Members) or {} end

--- The character the player controls now (the host when none says so).
function U.controlled()
    for _, m in ipairs(U.members()) do
        if try(Osi.IsControlled, m) == 1 then return m end
    end
    return Osi.GetHostCharacter()
end

--- The short handle of an entity in the bridge's answers: "#" and the first six characters of its uuid.
--- Every handle the bridge writes in blind mode is of a thing the mode let it tell (AI.Told): what
--- the agent was shown is what it may act on. A change of mode forgets them.
AI.Told = AI.Told or {}
function U.handle(id)
    local uuid = U.uuidOf(id) or tostring(id)
    if AI.Mode and AI.Mode.Blind and AI.Mode.Blind() then AI.Told[uuid] = true end
    return "#" .. uuid:sub(1, 6)
end

--- A text on one line, cut to `n` characters at a character boundary of UTF-8, with "..." when cut.
function U.cut(text, n)
    text = tostring(text or ""):gsub("%s+", " "):gsub("^ ", ""):gsub(" $", "")
    if #text <= n then return text end
    local last = n - 3
    while last > 0 and text:byte(last + 1) and text:byte(last + 1) >= 0x80 and text:byte(last + 1) < 0xC0 do last = last - 1 end
    return text:sub(1, last) .. "..."
end

function U.round1(v) return math.floor(v * 10 + 0.5) / 10 end

function U.posOf(id)
    local x, y, z = try(Osi.GetPosition, id)
    if x then return { x, y, z } end
end

function U.dist(a, b)
    local dx, dy, dz = a[1] - b[1], a[2] - b[2], a[3] - b[3]
    return math.sqrt(dx * dx + dy * dy + dz * dz)
end

-- ---------------------------------------------------------------------------------------
-- The mod's own rows and classes

local LUA = "Mods/Gwynerva_GateKitAI/ScriptExtender/Lua/"

local function readJson(path)
    local text = try(Ext.IO.LoadFile, LUA .. path, "data")
    if not text or text == "" then return nil end
    local ok, value = pcall(Ext.Json.Parse, text)
    return ok and value or nil
end
AI.ReadJson = readJson

local kinds = readJson("Shared/EventKinds.json")
local registered = kinds and GK.EventLog.Register and GK.EventLog.Register(kinds)
if not (registered and registered.ok) then
    Ext.Utils.PrintError("[GateKit AI] the family `ai` could not be added to GateKit's event log: "
        .. tostring(registered and registered.error or "no GK.EventLog.Register, or Shared/EventKinds.json unread"))
end

--- A row of the family `ai`. Never fails the caller: a row that cannot be written is dropped.
function AI.Row(kind, payload)
    local ok, row = pcall(GK.EventLog.Append, kind, payload)
    return ok and row or nil
end

AI.Class = {}
for path, entry in pairs((readJson("Shared/VerbClasses.json") or {}).verbs or {}) do AI.Class[path] = entry.class end

-- ---------------------------------------------------------------------------------------
-- The client's side of the picture, and acts carried out there

AI.Client = AI.Client or { open = {}, scenes = {}, seq = 0 }
AI.Asked = AI.Asked or {}      -- id -> done(answer): the acts the client has not answered yet
AI.NextAsk = AI.NextAsk or 0

AI.Channel = AI.Channel or Ext.Net.CreateChannel(ModuleUUID, "GKAI", function(msg)
    if type(msg) ~= "table" then return end
    local client = AI.Client
    if type(msg.open) == "table" then
        client.open, client.blocking = msg.open, msg.blocking
        client.seq = (client.seq or 0) + 1
    end
    if type(msg.scenes) == "table" then client.scenes = msg.scenes end
    if msg.stuck ~= nil then client.stuck = msg.stuck or nil end
    if type(msg.windows) == "table" then client.windows = msg.windows end
    if msg.hello then client.alive = GK.Ticks() end
    if msg.tool and msg.forward and AI.Forwarded then AI.Forwarded(msg) end
    if msg.id and AI.Asked[msg.id] then
        local done = AI.Asked[msg.id]
        AI.Asked[msg.id] = nil
        pcall(done, msg.answer or { ok = false, error = "the client answered nothing" })
    end
end)

--- Sends `act` ({ act = "resolve" | "cast" | "attack" | "look", ... }) to the client, which
--- carries it out in the UI's context and answers; `done(answer)` is called with its answer.
function AI.Ask(act, done)
    AI.NextAsk = AI.NextAsk + 1
    local id = AI.NextAsk
    act.id = id
    AI.Asked[id] = done
    local ok, problem = pcall(function() AI.Channel:Broadcast(act) end)
    if not ok then
        AI.Asked[id] = nil
        done({ ok = false, error = "the client could not be reached: " .. tostring(problem) })
    end
    return id
end
