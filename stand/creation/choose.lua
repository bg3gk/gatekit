-- Chooses an item of one of the creation window's lists by its Guid, through the list box that
-- shows it: the box whose ItemsSource is that collection takes the item as its SelectedItem, and the
-- game's own binding does the rest. UI context; the page that shows the list must be open.
-- GKSceneOptions = { choose = { from = "SelectableRaces", guid = "..." } } before the call
-- (collections of the window's view model: SelectableRaces, SelectableSubRaces, SelectableBackgrounds).
-- Run: ./gatekit --instance <copy> exec ui "GKSceneOptions = { choose = {...} } $(cat stand/creation/choose.lua)"
local C = GKSceneOptions and GKSceneOptions.choose
if not (C and C.from and C.guid) then return { ok = false, error = "GKSceneOptions.choose = { from, guid } is not set" } end
local widget = GK.Gui.Widget("CharacterCreation")
local data = GK.Gui.Data("CharacterCreation")
if not (widget and data) then return { ok = false, error = "the creation window is not open" } end
local collection = data[C.from]
local item, index
for i = 1, #collection do
    if collection[i].Guid == C.guid then item, index = collection[i], i break end
end
if not item then return { ok = false, error = "no item with that Guid in " .. C.from, count = #collection } end

-- Noesis userdata are not compared; a box shows the collection when its first item has the first Guid.
local first = collection[1].Guid
local budget, boxes = 20000, {}
local function walk(element, depth)
    if budget <= 0 or depth > 60 then return end
    budget = budget - 1
    local okType, kind = pcall(function() return tostring(element.Type) end)
    if okType and kind:find("ListBox$") then
        local okVisible, visible = pcall(function() return element.IsVisible end)
        local okFirst, guid = pcall(function() return element.ItemsSource[1].Guid end)
        -- The guardian's page shows the races through a filter: its ItemsSource has no items to
        -- read, and the box is known by what it has selected, an item of the same collection.
        local okSelected, selected = pcall(function() return element.SelectedItem.Guid end)
        local same = okFirst and guid == first
        if not same and not okFirst and okSelected and selected then
            for i = 1, #collection do if collection[i].Guid == selected then same = true break end end
        end
        if okVisible and visible and same then boxes[#boxes + 1] = element end
    end
    local okCount, count = pcall(function() return element.VisualChildrenCount end)
    for i = 1, (okCount and count or 0) do
        local okChild, child = pcall(function() return element:VisualChild(i) end)
        if okChild and child then walk(child, depth + 1) end
    end
end
walk(widget, 0)
if #boxes == 0 then return { ok = false, error = "no visible list shows " .. C.from .. " (open its page first)" } end
local ok, err = pcall(function() boxes[1].SelectedItem = item end)
local okNow, now = pcall(function() return boxes[1].SelectedItem.Guid end)
return { ok = ok and okNow and now == C.guid, error = (not ok) and tostring(err) or nil, selected = okNow and now or nil,
         index = index, of = #collection, boxes = #boxes }
