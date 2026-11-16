-- GK.Stand (server side): the video stand's bridge for an agent that plays - stand/README.md.
--
--   GK.Stand.Say(args)          the character's intent: a line over its head, and with `at` the
--                               accent on what the line is about; one row `stand_said`
--   GK.Stand.Accent(at, style)  the accent alone: a glow of the game's own, the camera's focus on
--                               it and back, a sound, the toolkit's marker when the style asks
--   GK.Stand.Feed(since, opts)  what changed since a cursor, by layers, short rows with a `text`
--   GK.Stand.Styles             data: presets by name, each a table of GK.Say.Styles' fields
--                               (the line) and the accent's (effect, pointEffect, sound, camera,
--                               hold, glide, marker, speakerEffect)
--
-- Built only from the toolkit's verbs: GK.Say (the line and the glow), GK.Camera (the focus),
-- GK.Focus (the marker), GK.See and GK.Heard (what the party knows), GK.EventLog (the rows).

GK.Stand = GK.Stand or {}
local Stand = GK.Stand

local function try(fn, ...)
    local ok, a, b, c = pcall(fn, ...)
    if ok then return a, b, c end
end

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
Stand.Styles = Stand.Styles or {}
Stand.Styles.intent = Stand.Styles.intent or {
    color = "#B340FF", bold = true, prefix = "« ", suffix = " »", accent = "#E0C8FF",
    effect = "orb", burst = "spectral", burstScale = 0.6, pointEffect = "spectral", sound = "faerie",
    camera = true, frame = "both", hold = 1.5, glide = true, marker = false }
-- a style table not named here starts from `intent` (or from `base`, a name)
Stand.Styles.danger = Stand.Styles.danger or {
    color = "#FF5040", bold = true, prefix = "♦ ", suffix = " ♦", accent = "#FFD040",
    effect = "faerie", pointEffect = "faerie_point", sound = "faerie", hold = 2, marker = true }
Stand.Styles.found = Stand.Styles.found or {
    color = "#FFD040", bold = true, prefix = "• ", suffix = " •", accent = "#B340FF" }
Stand.Styles.aside = Stand.Styles.aside or {
    color = "#D8C0FF", bold = false, italic = true, prefix = "~ ", suffix = " ~", accent = "#B340FF",
    effect = false, pointEffect = false, sound = false, camera = false, marker = false }
Stand.Default = Stand.Default or "intent"

-- The longest line kept over a head. The game draws a status's name on one line, centred, without
-- wrapping (92 characters drew whole in a 1024x576 window); the limit is what a watcher reads in
-- its ~3 s. Longer text is cut at a word, with "...".
Stand.MaxChars = Stand.MaxChars or 60

local LINE_FIELDS = { "color", "bold", "italic", "prefix", "suffix", "accent" }

--- A style by name or as a table over the default: the merged table, or nil and why.
local function styleOf(style)
    local base = Stand.Styles[Stand.Default] or {}
    local over = style
    if type(style) == "string" then
        over = Stand.Styles[style]
        if not over then return nil, "no style " .. style .. " in GK.Stand.Styles" end
    elseif style ~= nil and type(style) ~= "table" then
        return nil, "a style is a name of GK.Stand.Styles or a table of its fields"
    end
    local out = {}
    for k, v in pairs(base) do out[k] = v end
    -- a table may name its base: { base = "danger", color = ... }
    if type(over) == "table" and over.base and Stand.Styles[over.base] then
        for k, v in pairs(Stand.Styles[over.base]) do out[k] = v end
    end
    for k, v in pairs(type(over) == "table" and over or {}) do out[k] = v end
    return out
end

local function uuidOf(id)
    if type(id) ~= "string" then return nil end
    return id:match("(%x%x%x%x%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%x%x%x%x%x%x%x%x)$")
end

local function pointOf(t)
    if type(t) ~= "table" then return nil end
    local x, y, z = t.x or t[1], t.y or t[2], t.z or t[3]
    if type(x) == "number" and type(y) == "number" and type(z) == "number" then return { x, y, z } end
end

local function nameOf(id)
    if type(id) ~= "string" then return nil end
    return try(GK.See.Name, id) or id
end

local function controlled()
    for _, m in ipairs(GK.See.Members()) do
        if try(Osi.IsControlled, m) == 1 then return m end
    end
    return Osi.GetHostCharacter()
end

--- What `at` names: a point {x, y, z}, a uuid (or "Name_uuid"), a uuid's first 6+ characters, or a
--- name the party knows (a member, or a character or an object around within 60 m). Answers the
--- point or the uuid, or nil and why.
local function resolve(at)
    local p = pointOf(at)
    if p then return p end
    if type(at) ~= "string" or at == "" then return nil, "not a uuid, a name or a point: " .. tostring(at) end
    local id = uuidOf(at) or at
    if try(Osi.GetPosition, id) then return id end
    local prefix = at:match("^%x[%x%-]+$") and try(GK.See.Resolve, at)
    if prefix then return prefix end
    local wanted = at:lower()
    local best, bestD, part, partD
    local function consider(rowId, name, d)
        if not name then return end
        local n = name:lower()
        if n == wanted and (not bestD or (d or 0) < bestD) then best, bestD = rowId, d or 0 end
        if n:find(wanted, 1, true) and (not partD or (d or 0) < partD) then part, partD = rowId, d or 0 end
    end
    for _, m in ipairs(GK.See.Members()) do consider(m, nameOf(m), 0) end
    local around = try(GK.See.Around, { radius = 60, scope = "known", limit = 80 }) or {}
    -- what the party sees now first; what it remembers only when nothing seen has the name
    for _, remembered in ipairs({ false, true }) do
        for _, list in ipairs({ around.characters or {}, around.objects or {} }) do
            for _, row in ipairs(list) do
                if (row.remembered == true) == remembered and not row.gone then consider(row.id, row.name, row.d) end
            end
        end
        if best or part then return best or part end
    end
    return nil, "nothing the party knows is called " .. at
end

--- What `at` names, as GK.Stand.Say and Accent read it: a point {x, y, z}, a uuid (or
--- "Name_uuid"), a uuid's first 6+ characters, or a name the party knows (a member, a character or
--- an object around within 60 m, the nearest first). The point or the uuid, or nil and why.
function Stand.Resolve(at) return resolve(at) end

local function inDialogue()
    for _, m in ipairs(GK.See.Members()) do
        if GK.InDialog(m) then return true end
    end
    return false
end

--- The text cut to Stand.MaxChars at a word, with "..." when cut.
local function fit(text)
    text = tostring(text):gsub("%s+", " "):gsub("^ ", ""):gsub(" $", "")
    if #text <= Stand.MaxChars then return text, false end
    local cut = text:sub(1, Stand.MaxChars - 3)
    local atWord = cut:match("^(.*%S)%s+%S*$")
    if atWord and #atWord >= Stand.MaxChars / 2 then cut = atWord end
    return cut .. "...", true
end

-- The camera's answer, short: what a caller needs to know of it.
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
            -- with a line, the speaker and the thing framed together (the line stays in view)
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
--- to do) over the head of `args.who` (default: the controlled character) in a style of
--- GK.Stand.Styles (`args.style`: a name or a table over it; default `intent`); with `args.at` (a
--- uuid, a name the party knows, or a point {x, y, z}) the accent on it - GK.Stand.Accent's. A line
--- longer than GK.Stand.MaxChars is cut at a word. `args.think` (what the character thinks) and
--- `args["do"]` (the plain reason) are kept in the row only. Any of say and at may be left out,
--- not both. One row `stand_said`. Observed: { ok, who, say, cut, line = GK.Say.Line's short
--- answer, accent = { at, glow, sound, marker, camera } } once the camera came back (or at once
--- without a camera).
function Stand.Say(args)
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
    if GK.EventLog and GK.EventLog.Append then
        local at = answer.accent and answer.accent.at
        GK.EventLog.Append("stand_said", { who = uuidOf(who) or who, name = nameOf(who), say = answer.say,
            think = args.think, ["do"] = args["do"], at = type(at) == "string" and (uuidOf(at) or at) or at,
            atName = type(at) == "string" and nameOf(at) or nil,
            style = type(args.style) == "string" and args.style or (args.style and "custom") or Stand.Default })
    end
    return after(answer, camera)
end

--- The accent alone, for pointing at something without a line: on `at` (a uuid, a name the party
--- knows, or a point {x, y, z}) a short glow of the game's own (GK.Say.Accent), a sound, the
--- game's camera on it and back after the style's `hold` (skipped, without failing, while a
--- dialogue holds the camera), and GK.Focus's marker when the style asks. `style` as GK.Stand.Say's.
--- A row `stand_accent`. Observed: { ok, at, glow, sound, marker, camera } once the camera answered.
function Stand.Accent(at, style)
    local merged, why = styleOf(style)
    if not merged then return { ok = false, error = why } end
    local target, problem = resolve(at)
    if not target then return { ok = false, error = problem } end
    local parts, camera = accent(target, merged, controlled())
    if GK.EventLog and GK.EventLog.Append then
        GK.EventLog.Append("stand_accent", { at = type(target) == "string" and (uuidOf(target) or target) or target,
            atName = type(target) == "string" and nameOf(target) or nil,
            style = type(style) == "string" and style or (style and "custom") or Stand.Default })
    end
    if not camera then
        parts.ok = true
        return parts
    end
    -- flattened once the camera answered
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

-- ---------------------------------------------------------------------------------------
-- The feed

Stand.Cap = 12               -- rows per layer, newest last
Stand.Seen = Stand.Seen or {}   -- ids in `seen` at the last feed: what is new is marked

-- The client's open waiting states (a dialogue, a roll, a tutorial ...), sent by Client/Stand.lua
-- whenever one opens or closes: the server's own log does not hold them.
Stand.ClientOpen = Stand.ClientOpen or {}
Stand.Channel = Stand.Channel or Ext.Net.CreateChannel(ModuleUUID, "GK_Stand", function(msg)
    if type(msg) == "table" and type(msg.open) == "table" then GK.Stand.ClientOpen = msg.open end
end)

local function round1(v) return math.floor(v * 10 + 0.5) / 10 end

local function cap(list, layer, more)
    if #list > Stand.Cap then
        more[layer] = #list - Stand.Cap
        local out = {}
        for i = #list - Stand.Cap + 1, #list do out[#out + 1] = list[i] end
        return out
    end
    return list
end

local function cursorOf(since)
    if since == nil or since == "" then return nil, nil end
    if type(since) == "number" then return since, nil end
    local a, b = tostring(since):match("^(%d+):?(%d*)$")
    return tonumber(a), tonumber(b)
end

local function situation()
    local rows = {}
    local status = try(GK.See.Status) or {}
    local me = controlled()
    local place = try(GK.See.Place, me) or {}
    rows[#rows + 1] = { text = string.format("%s, %s: %s", nameOf(me) or "?", status.mode or "?", place.text or status.level or "?"),
                        who = uuidOf(me) }
    for _, m in ipairs(status.party or {}) do
        local weapon = try(Osi.GetEquippedItem, m.id, "Melee Main Weapon")
        local ranged = try(Osi.GetEquippedItem, m.id, "Ranged Main Weapon")
        local hands = {}
        if weapon then hands[#hands + 1] = nameOf(weapon) end
        if ranged then hands[#hands + 1] = nameOf(ranged) end
        rows[#rows + 1] = { who = uuidOf(m.id), text = string.format("%s %s/%s HP%s%s", m.name or "?", tostring(m.hp), tostring(m.maxHp),
            m.dead and ", dead" or "", #hands > 0 and (", holds " .. table.concat(hands, " and ")) or "") }
    end
    if status.mode == "combat" then
        local turn = try(GK.See.Turn) or {}
        local names = {}
        for _, t in ipairs(turn.turn or {}) do names[#names + 1] = t.name .. " (" .. tostring(t.rel) .. ")" end
        rows[#rows + 1] = { text = string.format("a fight, round %s; turn: %s", tostring(turn.round or status.round),
            #names > 0 and table.concat(names, ", ") or "nobody's yet"), who = turn.turn and turn.turn[1] and uuidOf(turn.turn[1].id) }
    end
    for _, t in ipairs(status.timers or {}) do
        rows[#rows + 1] = { text = string.format("countdown %s: %s turns left", t.text or t.name or "?", tostring(t.turnsLeft)) }
    end
    return rows
end

local function seen(options)
    local rows = {}
    local around = try(GK.See.Around, { radius = options.radius or 20, limit = 16 }) or {}
    local now = {}
    local list = {}
    for _, c in ipairs(around.characters or {}) do
        if c.rel ~= "party" then
            local does = {}
            if c.talk then does[#does + 1] = "talk" end
            if c.trade then does[#does + 1] = "trade" end
            if c.loot then does[#does + 1] = "loot" end
            list[#list + 1] = { d = c.d or 0, id = c.id, text = string.format("%s, %s%s, %s m %s%s", c.name or "someone", c.rel or "?",
                c.dead and " (dead)" or c.downed and " (down)" or c.inCombat and " (fighting)" or "", tostring(c.d), c.dir or "",
                #does > 0 and (" - " .. table.concat(does, ", ")) or "") }
        end
    end
    for _, o in ipairs(around.objects or {}) do
        local flags = {}
        if o.locked then flags[#flags + 1] = "locked" end
        if o.open then flags[#flags + 1] = "open" end
        if o.trap then flags[#flags + 1] = "trapped" end
        if o.owned then flags[#flags + 1] = "owned" end
        list[#list + 1] = { d = o.d or 0, id = o.id, text = string.format("%s (%s%s), %s m %s", o.name or "something", o.kind or "?",
            #flags > 0 and (", " .. table.concat(flags, ", ")) or "", tostring(o.d), o.dir or "") }
    end
    table.sort(list, function(a, b) return a.d < b.d end)
    for i = 1, math.min(#list, Stand.Cap) do
        local r = list[i]
        local id = uuidOf(r.id) or r.id
        now[id] = true
        rows[#rows + 1] = { text = r.text, target = id, new = not Stand.Seen[id] or nil }
    end
    if options.ways then
        local ways = try(GK.See.Ways, { range = 18 }) or {}
        local parts = {}
        for _, dir in ipairs({ "N", "NE", "E", "SE", "S", "SW", "W", "NW" }) do
            local w = ways.ways and ways.ways[dir]
            if w and (w.reach or 0) > 0 then parts[#parts + 1] = dir .. " " .. w.reach .. " m" end
        end
        rows[#rows + 1] = { text = "ways on: " .. (#parts > 0 and table.concat(parts, ", ") or "none seen") }
    end
    Stand.Seen = now
    return rows, #list - math.min(#list, Stand.Cap)
end

local function heard(from)
    local rows = {}
    local h = try(GK.Heard.Since, from or 0) or { lines = {}, last = from or 0 }
    for _, line in ipairs(h.lines or {}) do
        rows[#rows + 1] = { text = string.format("%s: %s", line.speaker or "someone", line.text),
            who = uuidOf(line.speakerId) or line.speakerId, d = line.d, dir = line.dir }
    end
    return rows, h.last
end

local function waitingText(w)
    local kind = w.what or w.kind
    if kind == "dialogue_choice" or kind == "dialogue_continue" or kind == "dialogue_line" then
        local answers = {}
        for i, a in ipairs(w.answers or {}) do
            answers[#answers + 1] = tostring(i) .. ") " .. tostring(type(a) == "table" and (a.text or a.Text) or a)
        end
        return string.format("%s: %s says \"%s\"%s", kind == "dialogue_choice" and "a dialogue waits for an answer"
            or kind == "dialogue_continue" and "a dialogue waits for a click on" or "a dialogue line is spoken",
            tostring(w.speaker or "someone"), tostring(w.text or ""), #answers > 0 and (" - " .. table.concat(answers, "; ")) or "")
    elseif kind == "roll" then
        return string.format("a roll window: %s%s, DC %s", tostring(w.check or w.skill or "a check"),
            w.who and (" for " .. tostring(nameOf(uuidOf(w.who) or w.who))) or "", tostring(w.dc))
    elseif kind == "combat_started" then
        return "a fight is on"
    end
    return string.format("%s%s waits", kind or "a window", w.window and (" (" .. tostring(w.window) .. ")") or "")
end

local function waiting()
    local rows = {}
    local seenKind = {}
    local server = {}
    for _, w in pairs(GK.Attention.Waiting) do server[#server + 1] = w end
    for _, w in ipairs(Stand.ClientOpen or {}) do server[#server + 1] = w end
    table.sort(server, function(a, b) return (a.seq or 0) < (b.seq or 0) end)
    for _, w in ipairs(server) do
        local kind = w.what or w.kind
        seenKind[kind or "?"] = true
        rows[#rows + 1] = { text = waitingText(w), kind = kind, key = w.key, who = w.who and (uuidOf(w.who) or w.who) or nil }
    end
    if inDialogue() and not (seenKind.dialogue or seenKind.dialogue_choice or seenKind.dialogue_continue or seenKind.dialogue_line) then
        rows[#rows + 1] = { text = "a dialogue is on (its window not read yet: bg3_dialog reads it)", kind = "dialogue" }
    end
    return rows
end

-- Which layer each kind of row goes to.
local LAYER = {
    fight_options = "about_to", fight_choice = "about_to", fight_action = "about_to",
    dice_thrown = "dice", dice_adjusted = "dice", game_roll = "dice",
    fight_joined = "happened", fight_left = "happened", round_started = "happened", concentration = "happened",
    fight_reaction = "happened", item_used = "happened", turn_ended = "happened",
    death = "happened", downed = "happened", combat_started = "happened", combat_ended = "happened",
    level_started = "happened", trade_requested = "happened", dialogue_started = "happened",
    stand_said = "said",
}
local GAME_SKIP = { weapon_state = true }

local function optionText(o)
    local target = o.target and nameOf(o.target) or (o.at and "a point") or nil
    return string.format("%s%s%s", tostring(o.spell or o.type or "?"), target and (" at " .. target) or "",
        o.chance and string.format(" (%d%%)", math.floor(o.chance * 100 + 0.5)) or "")
end

local function rowOf(row)
    local p = row.payload or {}
    local kind = row.kind
    local out = { seq = row.seq, kind = kind }
    out.who = p.who and (uuidOf(p.who) or p.who) or nil
    if kind == "fight_options" then
        local parts = {}
        for i = 1, math.min(3, #(p.options or {})) do parts[#parts + 1] = optionText(p.options[i]) end
        out.text = string.format("%s weighs: %s", nameOf(out.who) or "?", table.concat(parts, "; "))
        local best = p.options and p.options[1]
        out.target = best and best.target and (uuidOf(best.target) or best.target) or nil
        out.at = best and best.at or nil
    elseif kind == "fight_choice" or kind == "fight_action" then
        local target = p.target or (type(p.targets) == "table" and p.targets[1]) or nil
        if type(target) == "string" then out.target = uuidOf(target) or target else out.at = pointOf(target) and target or p.at end
        out.text = string.format("%s %s %s%s", nameOf(out.who) or "?", kind == "fight_choice" and "chooses" or "begins",
            tostring(p.spell or "to end its turn"), out.target and (" at " .. tostring(nameOf(out.target))) or out.at and " at a point" or "")
    elseif kind == "dice_thrown" then
        out.target = p.target and (uuidOf(p.target) or p.target) or nil
        out.text = string.format("%s rolled %s%s for %s%s%s", out.who and nameOf(out.who) or "the game", tostring(p.total),
            p.natural ~= nil and (" (die " .. (type(p.natural) == "table" and table.concat(p.natural, "+") or tostring(p.natural)) .. ")") or "",
            tostring(p.spell or p.status or p.what or p.kind or "?"), out.target and (" against " .. tostring(nameOf(out.target))) or "",
            p.dc and (", DC " .. tostring(p.dc)) or "")
    elseif kind == "dice_adjusted" then
        out.who = p.source and (uuidOf(p.source) or p.source) or out.who
        out.target = p.target and (uuidOf(p.target) or p.target) or nil
        local by = {}
        for _, b in ipairs(p.by or {}) do by[#by + 1] = tostring(b.interrupt) .. (b.owner and (" of " .. tostring(nameOf(uuidOf(b.owner) or b.owner))) or "") end
        out.text = string.format("a roll became %s (die %s)%s%s", tostring(p.total), tostring(p.natural),
            #by > 0 and (" by " .. table.concat(by, ", ")) or "", p.outcomeChanged and ": the outcome changed" or "")
    elseif kind == "stand_said" then
        out.text = string.format("%s: \"%s\"%s%s%s", p.name or "?", tostring(p.say or ""), p.atName and (" -> " .. p.atName) or "",
            p.think and (" | thinks: " .. p.think) or "", p["do"] and (" | does: " .. p["do"]) or "")
        out.target = type(p.at) == "string" and p.at or nil
        out.at = type(p.at) == "table" and p.at or nil
    elseif kind == "death" or kind == "downed" then
        out.text = string.format("%s %s", tostring(p.name or nameOf(out.who) or "?"), kind == "death" and "died" or "is down")
    elseif kind == "combat_started" then
        out.text = "a fight began"
    elseif kind == "combat_ended" then
        out.text = "the fight ended"
    else
        local text = p.text
        if type(text) ~= "string" or text == "" then text = kind .. (out.who and (": " .. tostring(nameOf(out.who))) or "") end
        out.text = text
        local by = p.by
        if type(by) == "string" then out.target = out.who; out.who = uuidOf(by) or by end
    end
    if out.who == nil then out.who = nil end
    return out
end

--- What changed since `since` (the `cursor` of an earlier answer; nil: the picture now and the
--- last rows kept), by layers - `situation`, `seen`, `heard`, `waiting`, `about_to`, `dice`,
--- `happened`, `said`, `scenario` - each a list of rows { text, who?, target?, at?, ... }, newest
--- last, at most GK.Stand.Cap per layer (`more` counts what was left out). `situation`, `seen`
--- and `waiting` are the present (a `seen` row is `new` when the last feed did not list it); the
--- others are the rows written since the cursor. Switches the families `dice` and `fight` on for
--- ten minutes again at every call. options: radius (of `seen`, 20), ways (true: the walkable
--- ways from the character, costs up to 1.5 s). { cursor, situation, ..., scenario = {}, more }.
function Stand.Feed(since, options)
    options = options or {}
    local L = GK.EventLog
    local families = {}
    for _, f in ipairs({ "dice", "fight" }) do
        if try(L.On, f, { seconds = 600 }) then families[#families + 1] = f end
    end
    local seq, heardFrom = cursorOf(since)
    local out = { cursor = nil, situation = situation(), more = {} }
    local seenRows, seenLeft = seen(options)
    out.seen = seenRows
    if seenLeft > 0 then out.more.seen = seenLeft end
    local heardRows, heardLast = heard(heardFrom or (seq == nil and math.max(0, (GK.Heard.Next or 1) - 1 - Stand.Cap) or 0))
    out.heard = cap(heardRows, "heard", out.more)
    out.waiting = waiting()
    local layers = { about_to = {}, dice = {}, happened = {}, said = {} }
    local from = seq or math.max(0, L.Seq - 600)
    local got = L.Since(from)
    for _, row in ipairs(got.rows) do
        local spec = L.Kinds[row.kind]
        local layer = LAYER[row.kind]
        if not layer and spec and spec.family == "game" and not GAME_SKIP[row.kind] and row.knows ~= "omniscient" then layer = "happened" end
        if layer then
            local ok, r = pcall(rowOf, row)
            if ok then layers[layer][#layers[layer] + 1] = r end
        end
    end
    for name, list in pairs(layers) do out[name] = cap(list, name, out.more) end
    out.scenario = {}
    if got.dropped then out.more.dropped = got.dropped end
    if next(out.more) == nil then out.more = nil end
    out.cursor = tostring(L.Seq) .. ":" .. tostring(heardLast or 0)
    out.families = families
    return out
end

return Stand
