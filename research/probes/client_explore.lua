-- Client side: what can be read about the UI, input and the controlled character.
local J = { StringifyInternalTypes = true, IterateUserdata = true, AvoidRecursion = true, MaxDepth = 4 }
local function plain(v) local ok, t = pcall(Ext.Json.Stringify, v, J); if ok then return Ext.Json.Parse(t) end; return "ERR " .. tostring(t) end
local out = {}

local ok, err = pcall(function()
    local sm = Ext.UI.GetStateMachine()
    out.rootState = tostring(sm.RootState)
    out.stateType = tostring(Ext.Types.GetObjectType(sm.State))
    out.state = plain(sm.State)
end)
if not ok then out.smError = tostring(err) end

ok, err = pcall(function()
    local im = Ext.ClientInput.GetInputManager()
    out.devices = plain(im.PerDeviceData)
    out.playerDevices = plain(im.PlayerDevices)
    out.deviceLists = plain(im.InputScheme.DeviceLists)
    out.allowDeviceEvents = im.AllowDeviceEvents
    out.injects = { device = #im.DeviceEventInjects, input = #im.InputInjects }
end)
if not ok then out.imError = tostring(err) end

ok, err = pcall(function()
    out.controlled = {}
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ClientControl")) do
        out.controlled[#out.controlled + 1] = { uuid = e.Uuid and e.Uuid.EntityUuid, name = e.DisplayName and e.DisplayName.Name and e.DisplayName.Name:Get() }
    end
end)
if not ok then out.controlError = tostring(err) end

out.inputType = (function()
    local t = {}
    for _, name in ipairs({ "EclInputController", "InputInputManager", "InputInjectInputData", "InputInputRawChange", "InputInputRaw" }) do
        local info = Ext.Types.GetTypeInfo(name)
        if info then
            local m = {}
            for k, v in pairs(info.Members) do m[k] = v.TypeName end
            t[name] = m
        end
    end
    return t
end)()
return out
