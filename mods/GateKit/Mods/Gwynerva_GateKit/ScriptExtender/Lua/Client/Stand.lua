-- GK.Stand (client side): the client's open waiting states (a dialogue, a roll window, a tutorial,
-- a reaction ...), sent to the server on GK_Stand whenever one opens or closes, so that the
-- server's GK.Stand.Feed lists them in its layer `waiting`. stand/README.md.

local L = GK.EventLog
local channel

local function short(text, n)
    if type(text) ~= "string" then return text end
    if #text > n then return text:sub(1, n - 3) .. "..." end
    return text
end

local FIELDS = { "window", "speaker", "text", "check", "skill", "dc", "who", "ready", "canTrade" }

local function openStates()
    local list = {}
    for key, row in pairs(L.Open) do
        local p = row.payload or {}
        local item = { key = key, kind = row.kind, seq = row.seq }
        for _, f in ipairs(FIELDS) do
            local v = p[f]
            if type(v) ~= "table" then item[f] = short(v, 160) end
        end
        if type(p.answers) == "table" then
            item.answers = {}
            for i, a in ipairs(p.answers) do
                if i > 8 then break end
                item.answers[i] = short(type(a) == "table" and (a.text or a.Text) or tostring(a), 80)
            end
        end
        list[#list + 1] = item
    end
    return list
end

local function send()
    if not channel then
        local ok, made = pcall(Ext.Net.CreateChannel, ModuleUUID, "GK_Stand")
        if not ok then return end
        channel = made
    end
    pcall(function() channel:SendToServer({ open = openStates() }) end)
end

--- The stand's listener on every row of the client's log: a row that opens or closes a waiting
--- state sends the open states to the server.
function L.Listeners.stand(row)
    if row.attention and row.attention ~= "news" then send() end
end
