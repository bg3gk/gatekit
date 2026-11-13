-- GK.Sound: how loud this copy of the game is. Client side, because sound belongs to the
-- client; each running copy has its own mixer state, so several copies can be told apart.
--
-- How the game does it. `ls::GlobalSwitches` holds `SoundSetting SoundSettings[12]`, one per
-- slider of the options screen: `{ float ConfigValue[3]; FixedString ConfigKey; HashSet<FixedString>
-- RTPCs; ... }`. The setting pushes `ConfigValue[1]` into its Wwise RTPCs, which is what the
-- mixer actually reads. Stock Script Extender does NOT publish `SoundSettings` (it is absent
-- from the property map, so `GlobalSwitches.SoundSettings` raises "property does not exist");
-- GateKit's build maps it, and `GK.Sound.Settings` reads it.
--
-- To set a volume GateKit writes the Wwise RTPCs directly, through `Ext.Audio.SetRTPC`, which is
-- the same value the slider ends up setting and needs no audio API of the operating system. The names
-- below are the game's own: the twelve `ConfigKey` strings and the eleven `RTPC_Volume_*` names
-- are read out of `bg3_dx11.exe` and verified live. Two consequences, both deliberate:
--
--   * the options screen does not move, and nothing is written back to the profile's
--     `config.lsf`, so what GateKit sets can never leak into a player's own settings. Those
--     settings are what `GK.Sound.Settings` reads; `GK.Sound.List` reads the mixer instead.
--   * the player (or the game, when it applies its settings) can overwrite a value by moving the
--     slider. The last writer wins; GateKit does not fight for it.
--
-- `RumbleVolume` is the twelfth setting and has no RTPC: it is controller vibration, not sound,
-- and the engine routes it elsewhere. It is listed with no value, so the twelve stay the twelve.
--
--   GK.Sound.List()              every category: key, name, rtpc, value (0..100)
--   GK.Sound.Get(category)       one value; GK.Sound.Set(category, value) sets it
--   GK.Sound.Master(value)       the master slider, read or written
--   GK.Sound.Mute(category)      to 0, remembering what it was; Unmute() puts it back
--   GK.Sound.Muted()             which categories are muted right now
--   GK.Sound.WhenUnfocused(on)   the game's own MuteSoundWhenNotFocused flag
--   GK.Sound.Settings()          the player's own settings, as the engine keeps them

GK.Sound = GK.Sound or {}
local Sound = GK.Sound

-- key: the game's ConfigKey; name: GateKit's short name; rtpc: the game's Wwise RTPC.
Sound.Categories = {
    { key = "MasterVolume",        name = "master",   rtpc = "RTPC_Volume_MAIN" },
    { key = "FxVolume",            name = "fx",       rtpc = "RTPC_Volume_SFX" },
    { key = "AmbientVolume",       name = "ambient",  rtpc = "RTPC_Volume_Ambient" },
    { key = "MusicVolume",         name = "music",    rtpc = "RTPC_Volume_Music" },
    { key = "MusicFightVolume",    name = "fight",    rtpc = "RTPC_Volume_Music_Fight" },
    { key = "CutsceneVolume",      name = "cutscene", rtpc = "RTPC_Volume_Cinematics" },
    { key = "UIVolume",            name = "ui",       rtpc = "RTPC_Volume_UI" },
    { key = "VoiceVolume",         name = "voice",    rtpc = "RTPC_Volume_VO_Master" },
    { key = "VoiceVolumeDialog",   name = "dialogue", rtpc = "RTPC_Volume_VO_Dialog" },
    { key = "VoiceVolumeNarrator", name = "narrator", rtpc = "RTPC_Volume_VO_Narrator" },
    { key = "VoiceVolumeOverhead", name = "overhead", rtpc = "RTPC_Volume_VO_Overhead" },
    { key = "RumbleVolume",        name = "rumble" },
}

local MIN, MAX = 0, 100

-- What Mute() took away, by the category's key, so Unmute() can give it back.
local silenced = {}

local function clamp(value)
    value = tonumber(value)
    if value == nil or value ~= value then return nil end
    return math.max(MIN, math.min(MAX, value))
end

--- The category whose game key, GateKit name or RTPC name is `category` (case does not matter),
--- or nil. Without a category the master is meant, so `GK.Sound.Get()` is the master volume.
function GK.Sound.Category(category)
    if category == nil or category == "" then return Sound.Categories[1] end
    if type(category) == "table" then return category end
    local wanted = tostring(category):lower()
    for _, entry in ipairs(Sound.Categories) do
        if entry.key:lower() == wanted or entry.name == wanted or (entry.rtpc and entry.rtpc:lower() == wanted) then
            return entry
        end
    end
    return nil
end

local function need(category)
    local entry = Sound.Category(category)
    if not entry then error("unknown sound category '" .. tostring(category) .. "'", 3) end
    return entry
end

--- Every sound category of the game, in the options screen's order: `{ key, name, rtpc, value,
--- muted }` each, where `value` is 0..100 and nil for a category with no RTPC (`RumbleVolume`).
function GK.Sound.List()
    local out = {}
    for i, entry in ipairs(Sound.Categories) do
        out[i] = {
            key = entry.key,
            name = entry.name,
            rtpc = entry.rtpc,
            value = entry.rtpc and Ext.Audio.GetRTPC(nil, entry.rtpc) or nil,
            muted = silenced[entry.key] ~= nil or nil,
        }
    end
    return out
end

--- How loud one category is, 0..100; nil for a category with no RTPC. `category` is the game's
--- key ("MusicVolume"), GateKit's short name ("music") or the RTPC name; nothing means the master.
function GK.Sound.Get(category)
    local entry = need(category)
    if not entry.rtpc then return nil end
    return Ext.Audio.GetRTPC(nil, entry.rtpc)
end

--- Sets one category to `value` (0..100, clamped) and returns what was set. Only this copy of
--- the game is affected, and only until something else writes the same RTPC. Forgets that the
--- category was muted, so a later `Unmute` will not undo this.
function GK.Sound.Set(category, value)
    local entry = need(category)
    local wanted = clamp(value)
    if wanted == nil then error("sound value must be a number between 0 and 100", 2) end
    if not entry.rtpc then error(entry.key .. " has no RTPC: it is not routed through the mixer", 2) end
    Ext.Audio.SetRTPC(nil, entry.rtpc, wanted)
    silenced[entry.key] = nil
    return wanted
end

--- The master volume: with `value` sets it, without reads it. Everything else is mixed under it.
function GK.Sound.Master(value)
    if value == nil then return Sound.Get("MasterVolume") end
    return Sound.Set("MasterVolume", value)
end

--- Silences a category, remembering how loud it was so `GK.Sound.Unmute` can restore it; without
--- a category the master is silenced, which silences this copy of the game. Muting twice keeps
--- the first remembered value. Returns what was remembered.
function GK.Sound.Mute(category)
    local entry = need(category)
    if not entry.rtpc then error(entry.key .. " has no RTPC: it is not routed through the mixer", 2) end
    if silenced[entry.key] == nil then silenced[entry.key] = Ext.Audio.GetRTPC(nil, entry.rtpc) end
    Ext.Audio.SetRTPC(nil, entry.rtpc, 0)
    return silenced[entry.key]
end

--- Puts back what `GK.Sound.Mute` took away: one category, or every muted one when no category
--- is named. Returns the keys that were restored. A category that was not muted is left alone,
--- so this never invents a volume of its own.
function GK.Sound.Unmute(category)
    local restored = {}
    local wanted = category ~= nil and category ~= "" and need(category) or nil
    for _, entry in ipairs(Sound.Categories) do
        local before = silenced[entry.key]
        if before ~= nil and (wanted == nil or wanted.key == entry.key) then
            Ext.Audio.SetRTPC(nil, entry.rtpc, before)
            silenced[entry.key] = nil
            restored[#restored + 1] = entry.key
        end
    end
    return restored
end

--- The categories `GK.Sound.Mute` is holding at 0, as `{ key = the volume remembered }`. Empty
--- when nothing is muted. A Lua reset empties it, and the volumes then stay where they were.
function GK.Sound.Muted()
    local out = {}
    for key, value in pairs(silenced) do out[key] = value end
    return out
end

--- The game's own `MuteSoundWhenNotFocused`: with `on` sets it, without reads it. When it is on
--- the game silences itself while its window is not the one in front - the cheapest "only the
--- copy I am looking at is heard" there is, since the game does it by itself. The engine reads
--- the flag when the window gains or loses focus, so setting it does not silence a copy that is
--- already in the background until focus next changes.
function GK.Sound.WhenUnfocused(on)
    local switches = Ext.Utils.GetGlobalSwitches()
    if on == nil then return switches.MuteSoundWhenNotFocused end
    switches.MuteSoundWhenNotFocused = on and true or false
    return Ext.Utils.GetGlobalSwitches().MuteSoundWhenNotFocused
end

--- The player's own sound settings, as the engine keeps them, beside the values `GK.Sound.List`
--- reads back out of the mixer. One row per category, in `GK.Sound.Categories` order:
--- `{ key, name, value, configured, default, display }`, where
---
---   * `configured` is the volume the player set - the one the engine reads out of the profile;
---   * `value` is the volume in effect, the one the engine writes to the category's RTPCs;
---   * `default` is the game's built-in default for the category (100 for all twelve);
---   * `display` is the row's name in the options screen, in the game's language.
---
--- `configured` and `value` come apart whenever the engine silences itself: with
--- `MuteSoundWhenNotFocused` on and the window behind another one, the master and the cutscene
--- settings read 100 and 0. To ask whether a copy is audible, read `value`.
---
--- A volume set through `GK.Sound.Set` is NOT among them: that writes the RTPC under the
--- setting and leaves the player's own settings alone, which is the point - `Settings()` says
--- what the player chose and `List()` says what is playing.
---
--- Needs GateKit's Script Extender build: the stock one leaves `SoundSettings` out of its
--- property map. Returns nil there, rather than raising.
function GK.Sound.Settings()
    local ok, settings = pcall(function() return Ext.Utils.GetGlobalSwitches().SoundSettings end)
    if not ok or settings == nil then return nil end
    -- the engine's array is not in the options screen's order, so go by the key
    local byKey = {}
    for i = 1, 12 do
        local setting = settings[i]
        if setting ~= nil then byKey[tostring(setting.ConfigKey)] = setting end
    end
    local out = {}
    for i, entry in ipairs(Sound.Categories) do
        local setting = byKey[entry.key]
        local values = setting and setting.ConfigValue
        local handle = setting and tostring(setting.DisplayName.Handle.Handle) or nil
        out[i] = {
            key = entry.key,
            name = entry.name,
            configured = values and values[1] or nil,
            value = values and values[2] or nil,
            default = values and values[3] or nil,
            display = handle and Ext.Loca.GetTranslatedString(handle) or nil,
        }
    end
    return out
end
