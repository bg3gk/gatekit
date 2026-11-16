-- GK.Say (server side): a line over a creature's head, and a short glow on a thing.
--
--   GK.Say.Line(who, text, options)   the text over the head of `who`
--   GK.Say.Accent(at, options)        a short effect of the game's own on a creature, an item or a point
--   GK.Say.Shown(who)                 the line a creature shows now
--   GK.Say.Styles, GK.Say.Effects, GK.Say.Sounds   data: a modder adds presets by name
--
-- How (game 4.1.1.7631656; night notes K1 and G1): the game writes a status's name over the head of
-- the creature it is applied to, for about three seconds, whatever the status's duration. So the
-- line is a status of the toolkit's own - a copy of BLESS with every effect stripped, flags
-- DisableCombatlog and DisablePortraitIndicator (no log line, no portrait icon) - whose display name
-- is a translated string of the toolkit's own, rewritten before each apply. The name takes the
-- game's text markup: <font color="#rrggbb">, <b>, <i>, several colours in one line, and the named
-- entities &lt; &gt; &amp; (numeric ones such as &#9733; are shown as written; `size` changes
-- nothing; a glyph the font lacks shows as a box: ✦ does, while « » • ♦ — ~ show). Debug text and
-- notifications show nothing over a head; Osi.OpenMessageBox blocks the game (never used).
-- Lines on two creatures at once need two names: a small pool of statuses and handles, one slot per
-- creature while its line shows. The status lives under a second (duration 1); its name floats on
-- for about three seconds after, and a longer duration does not lengthen the text (12 s tried).
-- The glow is Osi.PlayEffect / PlayEffectAtPosition with an effect resource: one-shot effects that
-- end by themselves, nothing to stop or clean (the status's ApplyEffect showed nothing with one). The translated string is changed in the process's localization, which the host's client
-- shares: a client of another machine would not see the text.

GK.Say = GK.Say or {}
local Say = GK.Say

-- Effects of the game's own (effect resources), short and ending by themselves. Measured in
-- Basic_Level_A, screenshots in the night notes G1.
Say.Effects = Say.Effects or {
    orb = "70ebf4d3-46f9-1cd2-7f59-15bd673f5bdb",       -- VFX_Characters_Gale_ActivateNethereseOrb_Cast_BodyFX_01: the body tinted violet, ~1 s
    spectral = "6d73ed12-1bb6-b0dd-9aff-09b78489362e",  -- VFX_Cinematic_Gale_SpectralVoice_Disappear_01: a violet-white burst on the ground, ~1.5 s
    faerie = "f713ffab-b047-914f-60b0-d5fc5b7f3a66",    -- VFX_Status_FaerieFire_BodyFX_Apply_01: a pale violet flare on the body
    faerie_point = "48937204-61a8-347b-b3be-d435e202fcbf", -- VFX_Spells_Cast_Intent_Utility_TargetAoE_FaerieFire_Impact_01: a spark at a point
    mystra = "19cecb15-96a2-ef10-8354-8ea13ba6aa80",    -- VFX_Status_LOW_StormshoreTabernacle_GodBlessed_Root_Apply_Mystra_01: a golden glyph over the head
}
-- Sound resources of the game's own, played on an object (Osi.PlaySoundResource).
Say.Sounds = Say.Sounds or {
    faerie = "99af6d03-39d3-497d-b459-fea1414a0227",    -- Spell_Impact_Debuff_FaerieFire_L1to3
}

-- The look of a line. `default` is the toolkit's own; a modder adds GK.Say.Styles.<name> = {...}.
-- Fields: color ("#rrggbb"), bold, italic, prefix, suffix (a glyph or a word around the text, in
-- the same colour unless `accent` names another), accent (the colour of prefix and suffix), effect
-- (a name of GK.Say.Effects or an effect resource's uuid, played on the creature as the line shows),
-- sound (a name of GK.Say.Sounds or a sound resource's uuid).
Say.Styles = Say.Styles or {}
Say.Styles.default = Say.Styles.default or { color = "#B340FF", bold = true }   -- the violet of GK.Focus's outline
Say.Styles.magic = Say.Styles.magic or { color = "#B340FF", bold = true, prefix = "• ", suffix = " •", accent = "#E0C8FF", effect = "orb" }
Say.Styles.whisper = Say.Styles.whisper or { color = "#C8A8FF", italic = true, bold = false, prefix = "~ ", suffix = " ~" }
Say.Styles.warning = Say.Styles.warning or { color = "#FF6040", bold = true, prefix = "♦ ", suffix = " ♦", accent = "#FFD040" }

Say.PoolSize = 8            -- lines shown at the same time, each on its own creature
Say.ShowMs = 3000           -- how long the game shows a status's name (measured ~3 s)
Say.Slots = Say.Slots or {}       -- slot -> { who, text, at }
Say.Prefix = "GK_SAY_"

local function try(fn, ...)
    local ok, value = pcall(fn, ...)
    if ok then return value end
end

local function now() return Ext.Utils.MonotonicTime() end

local function handleOf(slot)
    -- a handle of the toolkit's own, one per slot (the mod's uuid in its first groups)
    return string.format("h6b1d5c0dg7e57g4b1dg9e0ag%012d", slot)
end

--- The text escaped for the game's markup: < > & of the caller's text are shown as written.
local function escape(text)
    return (tostring(text):gsub("&", "&amp;"):gsub("<", "&lt;"):gsub(">", "&gt;"))
end

local function styleOf(options)
    local style = options.style
    if type(style) == "string" then
        if not Say.Styles[style] then return nil, "no style " .. style .. " in GK.Say.Styles" end
        style = Say.Styles[style]
    end
    local out = {}
    for k, v in pairs(Say.Styles.default) do out[k] = v end
    for k, v in pairs(type(style) == "table" and style or {}) do out[k] = v end
    for _, k in ipairs({ "color", "bold", "italic", "prefix", "suffix", "accent", "effect", "sound" }) do
        if options[k] ~= nil then out[k] = options[k] end
    end
    return out
end

local function markup(text, style)
    local body = escape(text)
    if style.italic then body = "<i>" .. body .. "</i>" end
    local function around(s)
        if not s or s == "" then return "" end
        s = escape(s)
        return style.accent and string.format('<font color="%s">%s</font>', style.accent, s) or s
    end
    local line = around(style.prefix) .. body .. around(style.suffix)
    if style.bold ~= false then line = "<b>" .. line .. "</b>" end
    return string.format('<font color="%s">%s</font>', style.color or "#B340FF", line)
end

local function resource(name, table_)
    if not name or name == false or name == "" then return nil end
    return table_[name] or name
end

-- A free slot (no line showing on it), else the oldest. A creature's next line takes another slot
-- than the one still showing: the same status applied again while its name floats shows nothing.
local function slotFor(who)
    local t, oldest, free = now(), nil, nil
    for i = 1, Say.PoolSize do
        local s = Say.Slots[i]
        if not s or t - s.at >= Say.ShowMs then free = free or i end
        if s and (not oldest or s.at < Say.Slots[oldest].at) then oldest = i end
    end
    return free or oldest or 1
end

local function statusFor(slot)
    local name = Say.Prefix .. slot
    local s = Ext.Stats.Get(name)
    if not s then s = Ext.Stats.Create(name, "StatusData", "BLESS") end
    s.DisplayName = handleOf(slot)
    s.StackId = name
    s.Boosts = ""
    s.StatusEffect = ""
    s.SoundLoop = ""
    s.ManagedStatusEffectGroup = ""
    s.ApplyEffect = ""
    s.StatusPropertyFlags = { "DisableCombatlog", "DisablePortraitIndicator" }
    s:Sync()
    return name
end

-- Takes a line's status off without the game's struck-through flash of its name: the name is
-- emptied first, so what flashes is nothing.
local function takeOff(who, slot)
    local name = Say.Prefix .. slot
    if try(Osi.HasActiveStatus, who, name) ~= 1 then return true end
    try(Ext.Loca.UpdateTranslatedString, handleOf(slot), "")
    try(Osi.RemoveStatus, who, name)
    return true
end

--- Shows `text` over the head of the creature `who` for about three seconds (the game's time for a
--- status's name), in a style: options.style is a name of GK.Say.Styles or a table of its fields
--- (color, bold, italic, prefix, suffix, accent, effect, sound), and any field may be given in
--- options directly. The caller's < > & are shown as written. Lines on several creatures at once
--- keep each its own. Tracked by the stage's ledger (GK.Stage.Clean takes a status still on off);
--- a row `said_overhead`. Observed: { ok, who, text, status, slot, handle, markup, ticks } once the
--- status is on the creature (it leaves in under a second; the name floats on for ~3 s).
function Say.Line(who, text, options)
    options = options or {}
    if type(who) ~= "string" or try(Osi.GetPosition, who) == nil then
        return { ok = false, error = "no such creature: " .. tostring(who) }
    end
    if text == nil or tostring(text) == "" then return { ok = false, error = "nothing to say" } end
    text = tostring(text)
    local style, why = styleOf(options)
    if not style then return { ok = false, error = why } end
    local slot = slotFor(who)
    local was = Say.Slots[slot]
    if was then takeOff(was.who, slot) end
    local line = markup(text, style)
    Ext.Loca.UpdateTranslatedString(handleOf(slot), line)
    local status = statusFor(slot)
    Say.Slots[slot] = { who = who, text = text, at = now(), status = status }
    Osi.ApplyStatus(who, status, 1, 1)
    -- the status's own ApplyEffect wants a multi-effect info, not an effect resource: an effect
    -- resource given there showed nothing; Osi.PlayEffect plays it
    local effect = resource(style.effect, Say.Effects)
    if effect then try(Osi.PlayEffect, who, effect, "", 1) end
    local sound = resource(style.sound, Say.Sounds)
    if sound then try(Osi.PlaySoundResource, who, sound) end
    if GK.Stage and GK.Stage.Track then
        GK.Stage.Track{ kind = "status", entity = who, note = "the line " .. status .. " over " .. who,
            undo = function() return takeOff(who, slot) end }
    end
    if GK.EventLog and GK.EventLog.Append then
        GK.EventLog.Append("said_overhead", { who = who, text = text, status = status, slot = slot,
            style = type(options.style) == "string" and options.style or nil, effect = style.effect or nil })
    end
    local answer = { ok = true, who = who, text = text, status = status, slot = slot, markup = line, handle = handleOf(slot) }
    return GK.Observe({ ok = true, waiting = "the status on the creature" }, function(ticks, final)
        if try(Osi.HasActiveStatus, who, status) == 1 then
            answer.ticks = ticks
            return answer
        end
        if final then return { ok = false, error = "the status never showed on " .. who, status = status } end
    end, 2000)
end

--- The line a creature shows now (within the game's ~3 s of a status's name), or nil:
--- { text, status, slot, active } - `active` whether the status is still on it.
function Say.Shown(who)
    local t, best = now(), nil
    for i = 1, Say.PoolSize do
        local s = Say.Slots[i]
        if s and s.who == who and t - s.at < Say.ShowMs and (not best or s.at > Say.Slots[best].at) then best = i end
    end
    local s = best and Say.Slots[best]
    if s then return { text = s.text, status = s.status, slot = best, active = try(Osi.HasActiveStatus, who, s.status) == 1 } end
end

--- A short effect of the game's own on a creature or an item (a uuid) or at a point {x, y, z}:
--- options.effect (a name of GK.Say.Effects or an effect resource's uuid; default "orb" on an
--- object, "spectral" at a point), options.sound (a name of GK.Say.Sounds or a sound resource;
--- played on the object, or on the host for a point), options.scale (default 1). The effects end
--- by themselves: nothing is left for GK.Stage.Clean. { ok, effect, at }.
function Say.Accent(at, options)
    options = options or {}
    local point
    if type(at) == "table" then
        local x, y, z = at.x or at[1], at.y or at[2], at.z or at[3]
        if type(x) == "number" and type(y) == "number" and type(z) == "number" then point = { x, y, z } end
        if not point then return { ok = false, error = "a point is {x, y, z}" } end
    elseif type(at) ~= "string" or try(Osi.GetPosition, at) == nil then
        return { ok = false, error = "no such object: " .. tostring(at) }
    end
    local effect = resource(options.effect or (point and "spectral" or "orb"), Say.Effects)
    local scale = options.scale or 1
    local ok, problem
    if point then
        ok, problem = pcall(Osi.PlayEffectAtPosition, effect, point[1], point[2], point[3], scale)
    else
        ok, problem = pcall(Osi.PlayEffect, at, effect, "", scale)
    end
    if not ok then return { ok = false, error = "the effect did not play: " .. tostring(problem) } end
    local sound = resource(options.sound, Say.Sounds)
    if sound then try(Osi.PlaySoundResource, point and Osi.GetHostCharacter() or at, sound) end
    return { ok = true, effect = effect, at = point or at }
end

return Say
