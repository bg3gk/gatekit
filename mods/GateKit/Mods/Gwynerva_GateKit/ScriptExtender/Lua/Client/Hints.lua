-- GK.Hints: the loading screen's hint list as the game holds it now. Client side: the list is the
-- client's (`code:ecl::HintManager`), filled once per process at the start from the game's
-- Public/Game/Hints/Hints.lsx and, with GateKit's Script Extender build, composed with every
-- enabled mod's Public/<Folder>/Hints/Hints.lsx (the fork's Docs/API.md, "Loading Hints").
--
-- How it is read. The list is no component and Script Extender maps no type for it, so it is read
-- from memory with Ext.Debug.ReadValue in the layout the registry gives (developer mode):
--   `code:ecl::HintManager::m_ptr` - the one manager of the process; the extender finds it
--     (its symbol `ecl__HintManager`) by the place App::InitializeResourceManager loads it;
--   `code:ecl::HintManager` - the array at +0x8, its count at +0x14;
--   `code:ecl::Hint` - 0x38 bytes: HintKey (FixedString) +0, the Description's handle +4,
--     Controller +0x14, Keyboard +0x15, WikiPage (STDString: its text inline up to 15 bytes, else
--     behind a pointer; length +0x10 of it, capacity +0x14) +0x18, Internal +0x30.
-- Who gave each entry is the fork's record of the composition (Ext.Hints.GetComposition), joined
-- by place and key.
--
--   GK.Hints.List()   every hint in the list's order: { index, key, handle, text, keyboard,
--                     controller, internal, wiki, source, mod?, replaced? }

GK.Hints = GK.Hints or {}
local Hints = GK.Hints

local Debug = Ext.Debug

-- The layout (registry: ecl::HintManager, ecl::Hint).
local ARRAY, COUNT = 0x8, 0x14
local SIZE = 0x38
local KEY, HANDLE, CONTROLLER, KEYBOARD, WIKI, INTERNAL = 0x0, 0x4, 0x14, 0x15, 0x18, 0x30
local WIKI_LENGTH, WIKI_CAPACITY, INLINE = 0x10, 0x14, 15
local MOST = 4096   -- a count above this is not a list: the manager was not where it was looked for

-- The address of the one ecl::HintManager, or nil and why.
local function manager()
    if not (Debug and Debug.ReadValue and Debug.GetSymbolAddress) then
        return nil, "this Script Extender build has no Ext.Debug.ReadValue and GetSymbolAddress (GateKit's build, developer mode)"
    end
    local at, why = Debug.GetSymbolAddress("ecl__HintManager")
    if not at then
        at, why = Debug.GetSymbolAddress("ecl::HintManager::m_ptr")
    end
    if not at then return nil, "the hint list's place is not known in this executable: " .. tostring(why) end
    local m = Debug.ReadValue(at, "uint64")
    if not m or m == 0 then return nil, "the hint list does not exist yet" end
    return m
end

-- The text of an STDString at `at`, or nil.
local function stdString(at)
    local length = Debug.ReadValue(at + WIKI_LENGTH, "uint32")
    local capacity = Debug.ReadValue(at + WIKI_CAPACITY, "uint32")
    if not length or not capacity or length > capacity or length > 4096 then return nil end
    if length == 0 then return "" end
    local bytes = at
    if capacity > INLINE then
        bytes = Debug.ReadValue(at, "uint64")
        if not bytes or bytes == 0 then return nil end
    end
    local out = {}
    for i = 0, length - 1 do
        local b = Debug.ReadValue(bytes + i, "uint8")
        if not b then return nil end
        out[#out + 1] = string.char(b)
    end
    return table.concat(out)
end

--- The loading screen's hint list as the game holds it now, in its order (the order the loading
--- screen draws from): { index, key, handle, text, keyboard, controller, internal, wiki, source,
--- mod?, replaced? } each. `text` is the description's handle translated by the game (nil when the
--- game has no text for it); `source` is "game" or the giving mod's UUID, `mod` that mod's name,
--- `replaced` whose entry of the key it replaced ({ source, name? }); without the fork's record of
--- the composition `source` is nil. nil and why when the list cannot be read.
function Hints.List()
    local m, why = manager()
    if not m then return nil, why end
    local array = Debug.ReadValue(m + ARRAY, "uint64")
    local count = Debug.ReadValue(m + COUNT, "uint32")
    if not array or not count or count > MOST then return nil, "the hint list's array cannot be read" end
    local composed = {}
    if Ext.Hints and Ext.Hints.GetComposition then
        local ok, c = pcall(Ext.Hints.GetComposition)
        if ok and c and c.Entries and #c.Entries == count then composed = c.Entries end
    end
    local list = {}
    for i = 0, count - 1 do
        local at = array + i * SIZE
        local key = Debug.ReadValue(at + KEY, "FixedString")
        local handle = Debug.ReadValue(at + HANDLE, "FixedString")
        local text
        if handle and handle ~= "" then
            local ok, t = pcall(Ext.Loca.GetTranslatedString, handle)
            if ok and t and t ~= "" and t ~= handle then text = t end
        end
        local h = { index = i + 1, key = key, handle = handle, text = text,
                    keyboard = Debug.ReadValue(at + KEYBOARD, "bool"),
                    controller = Debug.ReadValue(at + CONTROLLER, "bool"),
                    internal = Debug.ReadValue(at + INTERNAL, "bool"),
                    wiki = stdString(at + WIKI) }
        local e = composed[i + 1]
        if e and e.HintKey == key then
            h.source = e.Source == "Game" and "game" or e.Source
            if e.Mod then h.mod = e.Mod.Name end
            if e.Replaced then
                h.replaced = { source = e.Replaced.Source == "Game" and "game" or e.Replaced.Source, name = e.Replaced.Name }
            end
        end
        list[#list + 1] = h
    end
    return list
end

return Hints
