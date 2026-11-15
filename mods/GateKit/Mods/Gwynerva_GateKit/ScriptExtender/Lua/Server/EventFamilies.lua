-- The detailed families of the event log on the server (GK.EventLog.Families): status, entity,
-- surface, component. Each writes only while switched on (GK.EventLog.On) - for the length of a
-- check. The Osiris status events are listened to once (Osiris listeners cannot be removed) and
-- cost a table look while the family is off; the entity subscriptions exist only while on.

local L = GK.EventLog
if not L then return false end

local function bare(id)
    return type(id) == "string" and (id:match("(%x+%-%x+%-%x+%-%x+%-%x+)$") or id) or id
end

-- status: every status of every object, as Osiris raises it
L.StatusListened = L.StatusListened or {}
for event, kind in pairs({ StatusApplied = "status_applied", StatusRemoved = "status_removed" }) do
    if not L.StatusListened[event] then
        L.StatusListened[event] = pcall(Ext.Osiris.RegisterListener, event, 4, "after", function(object, status, causee)
            if not L.Switched.status then return end
            pcall(L.Append, kind, { who = bare(object), status = status, by = bare(causee) })
        end)
    end
end

-- entity, surface, component: the engine's own creation and destruction of components
local function uuidOf(entity)
    local ok, id = pcall(function() return entity.Uuid and entity.Uuid.EntityUuid end)
    return ok and id or nil
end

local function subscribe(family, components, created, destroyed)
    local handles = {}
    for _, component in ipairs(components) do
        local ok, h = pcall(Ext.Entity.OnCreateDeferred, component, function(entity)
            pcall(L.Append, created, { component = component, entity = tostring(entity), uuid = uuidOf(entity) })
        end)
        if ok and h then handles[#handles + 1] = h else error("cannot watch the creation of " .. tostring(component) .. ": " .. tostring(h)) end
        ok, h = pcall(Ext.Entity.OnDestroy, component, function(entity)
            pcall(L.Append, destroyed, { component = component, entity = tostring(entity), uuid = uuidOf(entity) })
        end)
        if ok and h then handles[#handles + 1] = h else error("cannot watch the destruction of " .. tostring(component) .. ": " .. tostring(h)) end
    end
    L.Subscriptions = L.Subscriptions or {}
    L.Subscriptions[family] = handles
end

local function unsubscribe(family)
    for _, h in ipairs(L.Subscriptions and L.Subscriptions[family] or {}) do pcall(Ext.Entity.Unsubscribe, h) end
    if L.Subscriptions then L.Subscriptions[family] = nil end
end

L.Hooks.entity = {
    on = function() subscribe("entity", { "ServerCharacter", "ServerItem" }, "entity_created", "entity_destroyed") end,
    off = function() unsubscribe("entity") end,
}
L.Hooks.surface = {
    on = function() subscribe("surface", { "ServerSurface" }, "surface_created", "surface_destroyed") end,
    off = function() unsubscribe("surface") end,
}
L.Hooks.component = {
    on = function(options)
        local names = options and options.components
        if type(names) ~= "table" or #names == 0 then
            error("the component family names its component types: GK.EventLog.On('component', { components = { 'Health', ... } })")
        end
        subscribe("component", names, "component_created", "component_destroyed")
    end,
    off = function() unsubscribe("component") end,
}

return true
