-- GK.Places: where the party is and where the journal points - the zone name the HUD shows and the
-- markers of the map (quest objectives, traders, waypoints). UI side (`ui` in the stand): it reads
-- the game's own view models, the same the map window and the minimap draw from.
--
--   GK.Places.Zone()       the zone's name ("" when the place has none, as on the Nautiloid)
--   GK.Places.Markers()    { { name, type, quest, pos = {x,y,z}, radius } } - every marker the map has
--
-- Found 2026-11-13 (game 4.1.1.7631656): CurrentPlayer (on any widget's data, here the hot bar's)
-- .SelectedCharacter.PlayerCharacterProperties.CurrentSubregionName is the zone (a translated
-- string handle); CurrentPlayer.WorldMap.Markers are ls.MapMarker {Name, Type ("QuestMarker" ...),
-- WorldPos (x, z), Height, Radius, JournalItemView.Item.Title (a handle)}.

GK.Places = GK.Places or {}
local Places = GK.Places

local function get(object, key)
    if object == nil then return nil end
    local ok, value = pcall(function() return object[key] end)
    return ok and value or nil
end

local function text(value)
    if value == nil then return nil end
    local s = tostring(value)
    if s == "" or s:find("HandleUnknown") then return nil end
    if s:match("^h%x+g%x+g%x+g%x+g%x+$") then
        local ok, t = pcall(Ext.Loca.GetTranslatedString, s)
        return ok and t ~= "" and t or nil
    end
    return s
end

local function player()
    local dc = GK.ViewModel and GK.ViewModel("gui::DCHotBar")
    return get(dc, "CurrentPlayer")
end

function Places.Zone()
    local props = get(get(player(), "SelectedCharacter"), "PlayerCharacterProperties")
    return text(get(props, "CurrentSubregionName")) or ""
end

function Places.Markers()
    local out = {}
    local markers = get(get(player(), "WorldMap"), "Markers")
    if not markers then return out end
    for i = 1, #markers do
        local m = markers[i]
        local wp = get(m, "WorldPos")
        local x, z = wp and (wp.x or wp[1]), wp and (wp.y or wp[2])
        local row = { name = text(get(m, "Name")) }
        if x and z then row.pos = { x, get(m, "Height") or 0, z } end
        local r = get(m, "Radius")
        if r and r > 0 then row.radius = r end
        local item = get(get(m, "JournalItemView"), "Item")
        row.quest = text(get(item, "Title"))
        -- markers without a quest are the party's own portraits on the map (names "ResStr_...")
        if row.quest or (row.name and not row.name:find("^ResStr_")) then out[#out + 1] = row end
    end
    return out
end

return Places
