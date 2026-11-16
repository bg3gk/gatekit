-- Turns the creation window to one of its pages, as its own buttons do: they set SelectedIndex of
-- the list `flowPage` (the game's CCLib_k.xaml: 3 = the name, 2 = the guardian) and run no command.
-- UI context. GKSceneOptions = { page = 3 } before the call.
-- Run: ./gatekit --instance <copy> exec ui "GKSceneOptions = { page = 3 } $(cat stand/creation/page.lua)"
local index = GKSceneOptions and GKSceneOptions.page
if type(index) ~= "number" then return { ok = false, error = "GKSceneOptions.page is not a number" } end
local widget = GK.Gui.Widget("CharacterCreation")
if not widget then return { ok = false, error = "the creation window is not open" } end
local budget, found = 20000, nil
local function walk(element, depth)
    if found or budget <= 0 or depth > 60 then return end
    budget = budget - 1
    local okName, name = pcall(function() return element.Name end)
    if okName and name == "flowPage" then found = element return end
    local okCount, count = pcall(function() return element.VisualChildrenCount end)
    for i = 1, (okCount and count or 0) do
        local okChild, child = pcall(function() return element:VisualChild(i) end)
        if okChild and child then walk(child, depth + 1) end
    end
end
walk(widget, 0)
if not found then return { ok = false, error = "no flowPage in the creation window" } end
local before = found.SelectedIndex
found.SelectedIndex = index
return { ok = found.SelectedIndex == index, before = before, page = found.SelectedIndex }
