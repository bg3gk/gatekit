-- Client side: try to make the game believe a controller button was pressed, by adding a raw
-- input change to the input manager's inject queue (the way InjectKeyPress works for keys).
local names = {}
for name, value in pairs(Ext.Enums.InputRawType) do
    if type(name) == "string" and name:lower():find("controller", 1, true) then names[#names + 1] = name end
end
table.sort(names)

local im = Ext.ClientInput.GetInputManager()
local button = ... or "controller_a"
local device = 1
local ok, err = pcall(function()
    for _, state in ipairs({ "Pressed", "Released" }) do
        im.InputInjects[#im.InputInjects + 1] = {
            Input = { DeviceId = device, InputId = button },
            Value = { State = state, Value = state == "Pressed" and 1.0 or 0.0, Value2 = 0.0 },
            InjectRawInput = true,
            Debug = false,
        }
    end
end)
return { ok = ok, err = not ok and tostring(err) or nil, queued = #im.InputInjects, controllerNames = table.concat(names, " ") }
