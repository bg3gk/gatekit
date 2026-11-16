-- AI.Say, AI.Accent, AI.Resolve (server side): the character's intent shown in the game.
--
--   AI.Say(args)          a line over the character's head, and with `at` the accent on what the
--                         line is about; one row `ai_said`
--   AI.Accent(at, style)  the accent alone: a glow of the game's own, the camera's focus on it and
--                         back, a sound, GateKit's marker when the style asks; one row `ai_accent`
--   AI.Resolve(at)        what a target names: a point, a uuid, a handle, a name the party knows
--   AI.Styles             data: presets by name, each a table of GK.Say.Styles' fields (the line)
--                         and the accent's (effect, pointEffect, sound, camera, hold, glide, marker,
--                         speakerEffect)
--
-- Built only from GateKit's verbs: GK.Say (the line and the glow), GK.Camera (the focus),
-- GK.Focus (the marker), GK.See (what the party knows).

local GK = AI.GK
local U = AI.Util
local try, uuidOf, pointOf, nameOf, controlled = U.try, U.uuidOf, U.pointOf, U.nameOf, U.controlled

-- The looks. A style's line fields go to GK.Say.Line; the rest shape the accent:
--   effect        a name of GK.Say.Effects (or an effect uuid) played on an entity `at`; false: none
--   burst         an effect played on the ground at an entity's feet besides (burstScale: its size)
--   pointEffect   the effect for a point `at`
--   speakerEffect played on the speaker as the line shows (false: none)
--   sound         a name of GK.Say.Sounds (or a sound uuid), on `at` (an entity) or the speaker
--   camera        true: the game's camera goes to `at` and comes back after `hold` seconds
--   glide         the camera glides even to a far `at` instead of cutting
--   frame         "both": with a line, the camera frames the speaker and `at` together (the line
--                 stays in view); otherwise `at` alone
--   marker        true: GK.Focus's violet outline (an object) or ring (a point) for `hold` + 1 s
AI.Styles = AI.Styles or {}
AI.Styles.intent = AI.Styles.intent or {
    color = "#B340FF", bold = true, prefix = "« ", suffix = " »", accent = "#E0C8FF",
    effect = "orb", burst = "spectral", burstScale = 0.6, pointEffect = "spectral", sound = "faerie",
    camera = true, frame = "both", hold = 1.5, glide = true, marker = false }
-- a style table not named here starts from `intent` (or from `base`, a name)
AI.Styles.danger = AI.Styles.danger or {
    color = "#FF5040", bold = true, prefix = "♦ ", suffix = " ♦", accent = "#FFD040",
    effect = "faerie", pointEffect = "faerie_point", sound = "faerie", hold = 2, marker = true }
AI.Styles.found = AI.Styles.found or {
    color = "#FFD040", bold = true, prefix = "• ", suffix = " •", accent = "#B340FF" }
AI.Styles.aside = AI.Styles.aside or {
    color = "#D8C0FF", bold = false, italic = true, prefix = "~ ", suffix = " ~", accent = "#B340FF",
    effect = false, pointEffect = false, sound = false, camera = false, marker = false }
AI.DefaultStyle = AI.DefaultStyle or "intent"

-- The longest line kept over a head. The game draws a status's name on one line, centred, without
-- wrapping; the limit is what a watcher reads in the three seconds the line shows. Longer text is
-- cut at a word, with "...".
AI.MaxChars = AI.MaxChars or 60

local LINE_FIELDS = { "color", "bold", "italic", "prefix", "suffix", "accent" }

--- A style by name or as a table over the default: the merged table, or nil and why.
local function styleOf(style)
    local base = AI.Styles[AI.DefaultStyle] or {}
    local over = style
    if type(style) == "string" then
        over = AI.Styles[style]
        if not over then return nil, "no style " .. style .. " in AI.Styles" end
    elseif style ~= nil and type(style) ~= "table" then
        return nil, "a style is a name of AI.Styles or a table of its fields"
    end
    local out = {}
    for k, v in pairs(base) do out[k] = v end
    if type(over) == "table" and over.base and AI.Styles[over.base] then
        for k, v in pairs(AI.Styles[over.base]) do out[k] = v end
    end
    for k, v in pairs(type(over) == "table" and over or {}) do out[k] = v end
    return out
end

local function resolve(at)
    local p = pointOf(at)
    if p then return p end
    if type(at) ~= "string" or at == "" then return nil, "not a handle, a name or a point: " .. tostring(at) end
    at = at:gsub("^#", "")
    local unknown = AI.Mode.Refuse("nothing the party sees or has seen is " .. at)
    local id = uuidOf(at) or at
    if try(Osi.GetPosition, id) then
        if not AI.Mode.Knows(id) then return nil, unknown end
        return id
    end
    local prefix = at:match("^%x[%x%-]+$") and #at >= 6 and try(GK.See.Resolve, at)
    if prefix then
        if not AI.Mode.Knows(prefix) then return nil, unknown end
        return prefix
    end
    local wanted = at:lower()
    local best, bestD, part, partD
    local function consider(rowId, name, d)
        if not name then return end
        local n = name:lower()
        if n == wanted and (not bestD or (d or 0) < bestD) then best, bestD = rowId, d or 0 end
        if n:find(wanted, 1, true) and (not partD or (d or 0) < partD) then part, partD = rowId, d or 0 end
    end
    for _, m in ipairs(U.members()) do consider(m, nameOf(m), 0) end
    local around = try(GK.See.Around, { radius = 60, scope = AI.Mode.Scope(true), limit = 80 }) or {}
    -- what the party sees now first; what it remembers or does not perceive only when nothing seen has the name
    for _, seen in ipairs({ true, false }) do
        for _, list in ipairs({ around.characters or {}, around.objects or {} }) do
            for _, row in ipairs(list) do
                local sees = not (row.remembered == true or row.perceived == false)
                -- of several with the name, a living one before a dead one, then the nearest
                if sees == seen and not row.gone then consider(row.id, row.name, (row.d or 0) + (row.dead and 1000 or 0)) end
            end
        end
        if best or part then return best or part end
    end
    if AI.Mode.Blind() then return nil, AI.Mode.Refuse("nothing the party sees or has seen is called " .. at) end
    return nil, "nothing around is called " .. at
end

--- What a target names: a point ({ x, y, z } or the text "x,y,z"), a uuid (or "Name_uuid"), a
--- handle of the bridge's answers ("#" and the first six characters of a uuid, or more of them), or
--- a name the party knows (a member, a character or an object around within 60 m, the nearest
--- first; a part of the name is enough). The point or the uuid, or nil and why.
function AI.Resolve(at) return resolve(at) end

local function inDialogue()
    for _, m in ipairs(U.members()) do
        if GK.InDialog(m) then return true end
    end
    return false
end
U.inDialogue = inDialogue

--- The text cut to AI.MaxChars at a word, with "..." when cut.
local function fit(text)
    text = tostring(text):gsub("%s+", " "):gsub("^ ", ""):gsub(" $", "")
    if #text <= AI.MaxChars then return text, false end
    local cut = text:sub(1, AI.MaxChars - 3)
    local atWord = cut:match("^(.*%S)%s+%S*$")
    if atWord and #atWord >= AI.MaxChars / 2 then cut = atWord end
    return cut .. "...", true
end

local function cameraShort(a)
    if type(a) ~= "table" then return nil end
    return { ok = a.ok, error = a.error, how = a.how, snapped = a.snapped, ticks = a.serverTicks or a.ticks }
end

--- The accent on `at` (already resolved) in a merged style, from `from` (the speaker, for the
--- sound when `at` is a point). Answers the parts' results and, when the camera goes, the camera's
--- observation (to be followed) as the second value.
local function accent(at, style, from, withLine)
    local out = { at = at }
    local point = pointOf(at)
    local effect
    if point then effect = style.pointEffect else effect = style.effect end
    if effect then
        local a = GK.Say.Accent(at, { effect = effect })
        out.glow = a.ok and (type(effect) == "string" and effect or true) or { ok = false, error = a.error }
    end
    if not point and style.burst then
        local x, y, z = try(Osi.GetPosition, at)
        if x then
            local a = GK.Say.Accent({ x, y, z }, { effect = style.burst, scale = style.burstScale or 1 })
            out.burst = a.ok and style.burst or { ok = false, error = a.error }
        end
    end
    if style.sound then
        local sound = GK.Say.Sounds[style.sound] or style.sound
        try(Osi.PlaySoundResource, point and (from or Osi.GetHostCharacter()) or at, sound)
        out.sound = style.sound
    end
    if style.marker and GK.Focus and GK.Focus.Show then
        local m = GK.Focus.Show(point and "ground" or "object", at, { seconds = (style.hold or 1.5) + 1 })
        out.marker = m.shown and (m.kind or true) or { ok = false, error = m.why }
    end
    local camera
    if style.camera and at ~= from then
        if inDialogue() then
            out.camera = { ok = false, error = "skipped: a dialogue holds the camera" }
        else
            local frame = at
            if withLine and style.frame == "both" and from then frame = { from, at } end
            camera = GK.Camera.Focus(frame, { glide = style.glide and true or nil, hold = style.hold or 1.5 })
        end
    end
    return out, camera
end

-- Answers `answer` once the camera's observation (if any) has its answer, with it as `camera`.
local function after(answer, camera)
    if not camera then return answer end
    local follow = GK.Follow(camera)
    return GK.Observe(answer, function(_, final)
        local a = follow()
        if a ~= nil then
            answer.accent.camera = cameraShort(a)
            return answer
        end
        if final then
            answer.accent.camera = { ok = false, error = "the camera did not answer" }
            return answer
        end
    end, 10000)
end

--- The character's intent, shown: `args.say` (a short line in the first person of what it is about
--- to do) over the head of `args.who` (default: the controlled character) in a style of AI.Styles
--- (`args.style`: a name or a table over it; default `intent`); with `args.at` (as AI.Resolve reads
--- it) the accent on it - AI.Accent's. A line longer than AI.MaxChars is cut at a word.
--- `args.think` (what the character thinks) and `args["do"]` (the plain reason) are kept in the
--- row only. Any of say and at may be left out, not both. One row `ai_said`. Observed: { ok, who,
--- say, cut, line = GK.Say.Line's short answer, accent = { at, glow, sound, marker, camera } } once
--- the camera came back (or at once without a camera).
function AI.Say(args)
    if type(args) == "string" then args = { say = args } end
    args = args or {}
    local style, why = styleOf(args.style)
    if not style then return { ok = false, error = why } end
    local who = args.who and resolve(args.who) or controlled()
    if type(who) ~= "string" then return { ok = false, error = "no such speaker: " .. tostring(args.who) } end
    local say = args.say ~= nil and tostring(args.say) or nil
    if (not say or say == "") and args.at == nil then return { ok = false, error = "nothing to say and nothing to point at" } end
    local answer = { ok = true, who = who }
    if say and say ~= "" then
        local text, cut = fit(say)
        local options = { style = {} }
        for _, k in ipairs(LINE_FIELDS) do options.style[k] = style[k] end
        options.style.effect = style.speakerEffect or false
        options.style.sound = false
        local line = GK.Unobserved(GK.Say.Line(who, text, options))
        local shown = line.ok and GK.Say.Shown(who) or {}
        answer.say, answer.cut = text, cut or nil
        answer.line = { ok = line.ok, error = line.error, status = shown.status, slot = shown.slot }
        if not line.ok then answer.ok = false end
    end
    local camera
    if args.at ~= nil then
        local at, problem = resolve(args.at)
        if not at then
            answer.accent = { ok = false, error = problem }
        else
            answer.accent, camera = accent(at, style, who, answer.say ~= nil)
        end
    end
    local at = answer.accent and answer.accent.at
    AI.Row("ai_said", { who = uuidOf(who) or who, name = nameOf(who), say = answer.say,
        think = args.think, ["do"] = args["do"], at = type(at) == "string" and (uuidOf(at) or at) or at,
        atName = type(at) == "string" and nameOf(at) or nil,
        style = type(args.style) == "string" and args.style or (args.style and "custom") or AI.DefaultStyle })
    return after(answer, camera)
end

--- The accent alone, for pointing at something without a line: on `at` (as AI.Resolve reads it) a
--- short glow of the game's own (GK.Say.Accent), a sound, the game's camera on it and back after
--- the style's `hold` (skipped, without failing, while a dialogue holds the camera), and GK.Focus's
--- marker when the style asks. `style` as AI.Say's. A row `ai_accent`. Observed: { ok, at, glow,
--- sound, marker, camera } once the camera answered.
function AI.Accent(at, style)
    local merged, why = styleOf(style)
    if not merged then return { ok = false, error = why } end
    local target, problem = resolve(at)
    if not target then return { ok = false, error = problem } end
    local parts, camera = accent(target, merged, controlled())
    AI.Row("ai_accent", { at = type(target) == "string" and (uuidOf(target) or target) or target,
        atName = type(target) == "string" and nameOf(target) or nil,
        style = type(style) == "string" and style or (style and "custom") or AI.DefaultStyle })
    if not camera then
        parts.ok = true
        return parts
    end
    local follow = GK.Follow(after({ ok = true, accent = parts }, camera))
    return GK.Observe({ ok = true, waiting = "the camera" }, function(_, final)
        local a = follow()
        if a ~= nil then
            local out = a.accent or {}
            out.ok = true
            return out
        end
        if final then parts.ok = true; return parts end
    end, 11000)
end
