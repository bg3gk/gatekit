-- GK.Attention, server side: the game's own events, as Osiris raises them, written as rows of the
-- event log (Shared/Attention.lua, Shared/EventLog.lua). Registered once per Lua state; the handlers are looked up at the moment
-- an event fires, so a reloaded file replaces them.

local A = GK.Attention
if not A then return false end

local function try(fn, ...)
    local ok, a = pcall(fn, ...)
    if ok then return a end
end

local function bare(id)
    return type(id) == "string" and (id:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id) or id
end

local function isMember(id)
    if GK.See and GK.See.IsMember then return try(GK.See.IsMember, id) == true end
    return try(Osi.IsPartyMember, id, 1) == 1
end

local function name(id)
    local handle = try(Osi.GetDisplayName, id)
    return handle and try(Osi.ResolveTranslatedString, handle) or nil
end

local function partyInvolved(dialog)
    local n = try(Osi.DialogGetNumberOfInvolvedPlayers, dialog) or 0
    return n > 0
end

A.On = {
    CombatStarted = function(combat)
        local host = try(Osi.GetHostCharacter)
        if host and try(Osi.IsInCombat, host) ~= 1 then
            -- a fight elsewhere in the level is not the party's
            A.Push("combat_elsewhere", { combat = tostring(combat) })
            return
        end
        A.Set("combat", { what = "combat_started", combat = tostring(combat) })
    end,
    CombatEnded = function(combat)
        local item = A.Waiting.combat
        if item and item.combat == tostring(combat) then A.Set("combat", nil, { combat = tostring(combat) }) end
    end,
    Died = function(character)
        local id = bare(character)
        if isMember(id) then A.Push("death", { who = id, name = name(id) }) end
    end,
    DownedChanged = function(character, downed)
        local id = bare(character)
        if downed == 1 and isMember(id) then A.Push("downed", { who = id, name = name(id) }) end
    end,
    DialogStarted = function(dialog, instance)
        if partyInvolved(instance) then A.Push("dialogue_started", { dialog = tostring(dialog), instance = instance }) end
    end,
    DialogEnded = function(dialog, instance)
        A.Push("dialogue_ended_server", { dialog = tostring(dialog), instance = instance })
    end,
    LevelGameplayStarted = function(level)
        A.Push("level_started", { level = tostring(level) })
    end,
    LevelUnloading = function(level)
        A.Push("level_unloading", { level = tostring(level) })
    end,
    TutorialBoxShown = function(...)
        A.Push("tutorial_shown")
    end,
    -- the story's call (TUT_CharacterCreation, five seconds into creation, when the user's
    -- tutorials are marked completed): the client then asks the player to resume them, if the
    -- profile has tips already shown
    ResetTutorialsRequest = function(user, ask)
        A.Push("tutorial_reset_request", { user = user, ask = ask == 1 })
    end,
    RequestTrade = function(character, trader)
        A.Push("trade_requested", { who = bare(character), trader = bare(trader), name = name(bare(trader)) })
    end,
}

local ARITY = { CombatStarted = 1, CombatEnded = 1, Died = 1, DownedChanged = 2, DialogStarted = 2, DialogEnded = 2,
                LevelGameplayStarted = 2, LevelUnloading = 1, RequestTrade = 4, ResetTutorialsRequest = 2 }
A.Registered = A.Registered or {}
for event, arity in pairs(ARITY) do
    if not A.Registered[event] then
        A.Registered[event] = pcall(Ext.Osiris.RegisterListener, event, arity, "after", function(...)
            local handler = GK.Attention.On[event]
            if handler then pcall(handler, ...) end
        end)
    end
end

return true
