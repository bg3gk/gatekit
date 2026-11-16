-- AI.Now (server side): the situation as the agent needs it, and only what changed since its
-- previous call.
--
--   AI.Now(options)     the `now` block: { where, fight, can, party, waiting, near, heard, events,
--                       scenes, route, more } - each key present only when it changed since the
--                       session's cursor (options.full: every key, the present whole)
--   AI.Encode(value, order)   JSON text on one line with the keys in a fixed order, UTF-8 as it is
--   AI.Limit, AI.Detail  the sizes: a hard cap of characters, the length of a list, of a text
--   AI.Sessions          the cursors, by session name
--
-- The cursor of a session is kept here, in the game: what the agent was last told. A load of a
-- save restarts Lua, so the first answer after it is a whole picture again.

local GK = AI.GK
local U = AI.Util
local try, uuidOf, nameOf, handle, cut, round1 = U.try, U.uuidOf, U.nameOf, U.handle, U.cut, U.round1

-- chars: the hard cap of a `now` block as JSON text; list: the longest list; text: the longest text.
AI.Detail = AI.Detail or {
    brief = { chars = 1200, list = 6, text = 120, radius = 25 },
    full = { chars = 6000, list = 30, text = 300, radius = 40 },
}
AI.Limit = AI.Limit or AI.Detail.brief
AI.Sessions = AI.Sessions or {}

local KEYS = { "where", "fight", "can", "party", "waiting", "near", "heard", "events", "rolls", "scenes", "route", "notes", "prompt", "more" }
AI.NowKeys = KEYS

-- ---------------------------------------------------------------------------------------
-- JSON with the keys in order

local ESCAPES = { ['"'] = '\\"', ["\\"] = "\\\\", ["\n"] = "\\n", ["\r"] = "\\r", ["\t"] = "\\t" }

local function quote(s)
    return '"' .. s:gsub('[%c"\\]', function(c) return ESCAPES[c] or string.format("\\u%04x", c:byte()) end) .. '"'
end

local function encode(value, order, object)
    local kind = type(value)
    if kind == "string" then return quote(value) end
    if kind == "number" then
        if value == math.floor(value) and math.abs(value) < 1e15 then return string.format("%d", value) end
        return tostring(round1(value))
    end
    if kind == "boolean" then return value and "true" or "false" end
    if kind ~= "table" then return "null" end
    if #value > 0 or next(value) == nil then
        if object then return "{}" end
        local parts = {}
        for i, v in ipairs(value) do parts[i] = encode(v) end
        return "[" .. table.concat(parts, ",") .. "]"
    end
    local keys, seen = {}, {}
    for _, k in ipairs(order or {}) do
        if value[k] ~= nil then keys[#keys + 1] = k; seen[k] = true end
    end
    local rest = {}
    for k in pairs(value) do
        if type(k) == "string" and not seen[k] then rest[#rest + 1] = k end
    end
    table.sort(rest)
    for _, k in ipairs(rest) do keys[#keys + 1] = k end
    local parts = {}
    for _, k in ipairs(keys) do parts[#parts + 1] = quote(k) .. ":" .. encode(value[k], k == "now" and KEYS or nil, k == "now" or k == "more") end
    return "{" .. table.concat(parts, ",") .. "}"
end

--- JSON text of `value` on one line: an object's keys in `order` first and the rest sorted, a
--- `now` field in the order of AI.NowKeys, text as UTF-8 (only quotes, backslashes and control
--- characters escaped).
function AI.Encode(value, order) return encode(value, type(order) == "table" and order or KEYS, true) end

-- ---------------------------------------------------------------------------------------
-- The parts, each as it is now

local function memberSet()
    local set = {}
    for _, m in ipairs(U.members()) do set[uuidOf(m) or m] = true end
    return set
end

local function where()
    local me = U.controlled()
    local p = U.posOf(me)
    local place = try(GK.See.Place, me) or {}
    local at = p and string.format("%s,%s,%s", round1(p[1]), round1(p[2]), round1(p[3])) or "?"
    local by = place.near and (", by " .. tostring(place.near):gsub(" %(.-%)$", "")) or ""
    return { text = string.format("%s%s; %s at %s", tostring(place.level or "?"), by, nameOf(me) or "?", at), level = place.level, p = p }
end

--- Whose turn it is: { on, round, turn = { { id, name, mine } }, mine = the first party member whose turn is open }.
function AI.Turn()
    local turn = try(GK.See.Turn) or {}
    if not turn.inFight then return { on = false } end
    local party = memberSet()
    local out = { on = true, round = turn.round, turn = {} }
    for _, t in ipairs(turn.turn or {}) do
        local id = uuidOf(t.id) or t.id
        local mine = party[id] == true
        out.turn[#out.turn + 1] = { id = id, name = t.name, mine = mine }
        if mine and not out.mine then out.mine = id end
    end
    return out
end

local function fight()
    local t = AI.Turn()
    if not t.on then return { text = "off", sig = "off", turn = t } end
    local names, ids = {}, {}
    for _, row in ipairs(t.turn) do
        names[#names + 1] = tostring(row.name) .. (row.mine and " (mine)" or "")
        ids[#ids + 1] = row.id
    end
    local text = string.format("round %s, turn: %s", tostring(t.round), #names > 0 and table.concat(names, ", ") or "nobody's yet")
    for _, timer in ipairs(try(GK.See.Timers) or {}) do
        text = text .. string.format("; countdown %s: %s turns left", cut(timer.text or timer.name or "?", 30), tostring(timer.turnsLeft))
    end
    return { text = text, sig = text, turn = t, turnSig = tostring(t.round) .. "|" .. table.concat(ids, ",") }
end

-- What the member whose turn is open can do to each enemy the party perceives, the best first.
local function can(actor, limits)
    local out = {}
    local targets = try(GK.See.Targets, actor) or {}
    for i, target in ipairs(targets) do
        if i > limits.list then break end
        local options = {}
        for j = 1, math.min(2, #(target.options or {})) do
            local o = target.options[j]
            options[#options + 1] = string.format("%s%s%s", tostring(o.id),
                o.chance and string.format(" %d%%", math.floor(o.chance * 100 + 0.5)) or "",
                (o.move or 0) > 0 and string.format(" after %s m", round1(o.move)) or "")
        end
        if #options > 0 then
            out[#out + 1] = string.format("%s %s %s hp: %s", tostring(target.name), handle(target.id), tostring(target.hp), table.concat(options, " | "))
        end
    end
    return out
end

local function party()
    local out = {}
    for _, m in ipairs((try(GK.See.Status) or {}).party or {}) do
        local hands = {}
        for _, slot in ipairs({ "Melee Main Weapon", "Ranged Main Weapon" }) do
            local item = try(Osi.GetEquippedItem, m.id, slot)
            if item then hands[#hands + 1] = nameOf(item) end
        end
        local conditions = {}
        for i, s in ipairs(try(GK.See.Statuses, m.id) or {}) do
            if i > 3 then break end
            conditions[#conditions + 1] = s.name or s.id
        end
        out[#out + 1] = string.format("%s %s/%s hp%s%s%s", tostring(m.name), tostring(m.hp), tostring(m.maxHp), m.dead and ", dead" or "",
            #hands > 0 and (", holds " .. table.concat(hands, " + ")) or "",
            #conditions > 0 and (", " .. table.concat(conditions, ", ")) or "")
    end
    -- the same order whoever is controlled: a change of control is not a change of the party
    table.sort(out)
    return out
end

local NOTABLE = { door = true, ladder = true, portal = true, usable = true, container = true }

-- Who and what of note the party perceives: id -> { sig, text, d }.
local function near(limits, inFight, wide)
    local out = {}
    -- a step's block lists what the party perceives; a look in open mode, everything within the radius
    local around = try(GK.See.Around, { radius = limits.radius, limit = 40, scope = wide and AI.Mode.Scope(false) or "seen" }) or {}
    for _, c in ipairs(around.characters or {}) do
        if c.rel ~= "party" and not c.remembered then
            local id = uuidOf(c.id) or c.id
            local state = c.dead and "dead" or c.downed and "down" or c.perceived == false and "unseen" or nil
            local hp = (c.rel == "hostile" and inFight and c.hp) and (tostring(c.hp) .. " hp") or nil
            local does = c.dead and c.loot and "loot" or c.talk and not c.dead and c.rel ~= "hostile" and "talk" or nil
            local words = { tostring(c.rel or "?") }
            if state then words[#words + 1] = state end
            if hp then words[#words + 1] = hp end
            if does then words[#words + 1] = "can " .. does end
            local head = string.format("%s %s %s", tostring(c.name or "someone"), handle(id), table.concat(words, ", "))
            out[id] = { sig = head, text = string.format("%s, %s m %s", head, tostring(c.d), tostring(c.dir or "")), d = c.d or 0 }
        end
    end
    for _, o in ipairs(around.objects or {}) do
        if NOTABLE[o.kind] and not o.remembered then
            local id = uuidOf(o.id) or o.id
            local words = { tostring(o.kind) }
            if o.locked then words[#words + 1] = "locked" end
            if o.open then words[#words + 1] = "open" end
            if o.trap then words[#words + 1] = "trapped" end
            local head = string.format("%s %s (%s)", tostring(o.name or "something"), handle(id), table.concat(words, ", "))
            out[id] = { sig = head, text = string.format("%s, %s m %s", head, tostring(o.d), tostring(o.dir or "")), d = o.d or 0 }
        end
    end
    return out
end

local function answersOf(list, limits)
    local out = {}
    for i, a in ipairs(list or {}) do
        local text = type(a) == "table" and (a.text or a.Text) or a
        local n = type(a) == "table" and tonumber(a.n) or i
        out[#out + 1] = tostring(n) .. ") " .. cut(tostring(text), math.floor(limits.text * 0.75))
    end
    return out
end

local PHASES = { dialogue_choice = true, dialogue_continue = true, dialogue_line = true }
-- What the server's own log holds open and the bridge says elsewhere, or that waits for nothing.
local QUIET = { combat_started = true }
local HOW = {
    tutorial = "do=close", loot = "do=take (all of it) or do=close", book = "do=close", trade = "do=close",
    learn = "do=close", pickpocket = "do=close", save = "do=close", load = "do=close", menu = "do=close",
    rest = "do=answer n=1 (rest) or do=close", split = "do=close", window = "do=close",
    reward = "do=answer n=<number>", reaction = "do=answer n=<number>, n=0 declines", box = "do=answer n=<number>",
}

--- What waits for the player: { list = texts, actionable = one of them can be answered now,
--- busy = why the game goes on by itself (a line being spoken, a film, a load), kinds }.
function AI.Waiting(limits)
    limits = limits or AI.Limit
    local items = {}
    for _, w in pairs(GK.Attention.Waiting) do items[#items + 1] = w end
    for _, w in ipairs(AI.Client.open or {}) do items[#items + 1] = w end
    table.sort(items, function(a, b) return (a.seq or 0) < (b.seq or 0) end)
    local out = { list = {}, kinds = {} }
    local phase
    for _, w in ipairs(items) do
        local kind = w.what or w.kind
        if PHASES[kind] then phase = w end
        out.kinds[kind or "?"] = true
    end
    local blocking = AI.Client.blocking
    for _, w in ipairs(items) do
        local kind = w.what or w.kind
        local text
        if kind == "dialogue_choice" and #(w.answers or {}) > 1 then
            text = string.format("%s: \"%s\" %s -> do=answer n=<number>", tostring(w.speaker or "someone"), cut(w.text or "", limits.text * 2),
                table.concat(answersOf(w.answers, limits), " "))
            out.actionable, out.choice, out.spoken = true, #w.answers, w.text
        elseif kind == "dialogue_continue" or kind == "dialogue_choice" then
            text = string.format("%s: \"%s\" -> do=continue", tostring(w.speaker or "someone"), cut(w.text or "", limits.text * 2))
            out.actionable, out.spoken = true, w.text
        elseif kind == "dialogue_line" then
            out.busy = "a line is being spoken"
        elseif kind == "dialogue" then
            if not phase then out.busy = "a dialogue is on" end
        elseif kind == "roll" then
            text = string.format("a roll window: %s%s%s -> do=roll", tostring(w.check or w.skill or "a check"),
                w.dc and (", DC " .. tostring(w.dc)) or "", w.who and (", " .. tostring(nameOf(uuidOf(w.who) or w.who)) .. " rolls") or "")
            out.actionable = true
        elseif kind == "gameover" then
            text = "the game is over: the party is dead. Do nothing; tell the owner"
            out.actionable = true
        elseif kind == "film" or kind == "loading" or kind == "saving" then
            out.busy = kind == "film" and "a film plays" or "the game is " .. kind
        elseif kind and not QUIET[kind] then
            local detail = ""
            if type(blocking) == "table" and blocking.window == (w.window or kind) and blocking.text then
                detail = ": " .. cut(blocking.text, limits.text)
                if blocking.answers then detail = detail .. " " .. table.concat(answersOf(blocking.answers, limits), " ") end
            end
            text = string.format("a %s window%s -> %s", tostring(w.window or kind), detail, HOW[kind] or "do=close")
            out.actionable = true
        end
        if text then out.list[#out.list + 1] = text end
    end
    if not out.actionable and not out.busy and U.inDialogue() then out.busy = "a dialogue is on" end
    return out
end

local EVENTS = {
    game_hit = true, game_death = true, game_down = true, game_revive = true, game_fight = true, game_quest = true,
    game_level = true, game_heal = true, game_item = true, game_approval = true, death = true, downed = true,
    level_started = true, trade_requested = true,
}

-- Rows since `from`: what happened (hits, deaths, quest news) and, where the mode tells them, the
-- rolls thrown (the family `dice`, omniscient: the game shows a player only the roll window).
local function events(from)
    local out, rolls = {}, {}
    local L = GK.EventLog
    local tellRolls = AI.Mode.Part("rolls")
    if tellRolls and not (L.Switched and L.Switched.dice) then try(L.On, "dice", { seconds = 600 }) end
    for _, row in ipairs(L.Since(from).rows) do
        if row.kind == "dice_thrown" and tellRolls then
            local p = row.payload or {}
            local text = (type(p.text) == "string" and p.text ~= "" and p.text) or string.format("%s rolled %s%s%s",
                tostring(p.who or "the game"), tostring(p.total),
                p.spell and (" for " .. tostring(p.spell)) or p.what and (" for " .. tostring(p.what)) or "", p.dc and (" against " .. tostring(p.dc)) or "")
            -- the roller by its name
            rolls[#rolls + 1] = text:gsub("%x%x%x%x%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%x%x%x%x%x%x%x%x", function(id) return tostring(nameOf(id)) end)
        elseif EVENTS[row.kind] and AI.Mode.Row(row) then
            local p = row.payload or {}
            local text = p.text
            if type(text) ~= "string" or text == "" then
                text = row.kind .. (p.name and (": " .. tostring(p.name)) or p.level and (": " .. tostring(p.level)) or "")
            end
            out[#out + 1] = text
        end
    end
    return out, L.Seq, rolls
end

-- Lines spoken since `from`, without the game's markup; the line `spoken` (the one a dialogue
-- waits on, said in `waiting`) is not said twice.
local function heard(from, spoken)
    local out = {}
    local h = try(GK.Heard.Since, from) or { lines = {}, last = from }
    local skip = type(spoken) == "string" and spoken:gsub("%s+", " "):sub(1, 40) or nil
    for _, line in ipairs(h.lines or {}) do
        local text = tostring(line.text):gsub("<[^>]+>", ""):gsub("%s+", " ")
        if not (skip and text:sub(1, #skip) == skip) then
            out[#out + 1] = string.format("%s: %s", tostring(line.speaker or "someone"), text)
        end
    end
    return out, h.last or from
end

local function scenes()
    local out = AI.SceneLines(Mods.GateKit)
    for name, line in pairs(AI.Client.scenes or {}) do
        if out[name] == nil then out[name] = line end
    end
    return out
end

-- ---------------------------------------------------------------------------------------
-- The block

local function sessionOf(name)
    name = name or "default"
    local s = AI.Sessions[name]
    if not s then
        s = { first = true, seq = GK.EventLog.Seq, heard = math.max(0, (GK.Heard.Next or 1) - 1), near = {}, scenes = {} }
        AI.Sessions[name] = s
    end
    return s
end

local function lastOf(list, n, text)
    local out = {}
    for i = math.max(1, #list - n + 1), #list do out[#out + 1] = cut(list[i], text) end
    return out, math.max(0, #list - n)
end

local function sameList(a, b)
    if #a ~= #b then return false end
    for i = 1, #a do if a[i] ~= b[i] then return false end end
    return true
end

local function sortedNames(map)
    local names = {}
    for name in pairs(map) do names[#names + 1] = name end
    table.sort(names)
    return names
end

--- The `now` block. Without options: what changed since the previous call of the session - a key
--- is there only when its part changed, so a call after which nothing happened answers an empty
--- block - and the session's cursor moves. The first call of a session, and any call with
--- options.full, answers every part as it is now.
---   where    the level, the nearest named thing, the controlled character and its position;
---            again after two metres or more
---   fight    "off", or the round, whose turn is open (`(mine)`: a party member's) and the story's
---            countdowns
---   can      when a party member's turn opens: for each enemy the party perceives, the best
---            actions with the game's own chance
---   party    each member: hit points, what is in hand, conditions; when any of it changed
---   waiting  what the game waits for from the player, each with the step that answers it; an
---            empty list when what waited is gone
---   near     creatures and usable things of note the party perceives: `+` new, `~` changed; each
---            with its handle, how it stands to the party, distance and bearing
---   heard    lines spoken since
---   events   hits, deaths, quest news since, one line each
---   rolls    open mode: every roll thrown since
---   scenes   open mode: the state of each table under GKScene that changed
---   route    open mode: the plan's next step, when it changed
---   notes    the agent's own notes written near here (AI.Notes), each once per coming near
---   more    how many entries of a list were left out
--- options: session (a name; default "default"), full (true: the whole picture), detail ("brief"
--- or "full": the sizes of AI.Detail), peek (true: the cursor stays), part (one key: only it),
--- wide (true: `near` by the mode's widest scope - in open mode also what the party does not perceive).
--- Answers the block, and its length as JSON text as the second value.
function AI.Now(options)
    options = options or {}
    local limits = AI.Detail[options.detail or ""] or AI.Limit
    local s = sessionOf(options.session)
    local full = options.full or s.first
    local raw, commit = {}, {}

    local w = where()
    if full or w.level ~= s.level or not (w.p and s.p) or U.dist(w.p, s.p) >= 2 then
        raw.where = w.text
        commit.level, commit.p = w.level, w.p
    end

    local f = fight()
    if full or f.sig ~= s.fight then raw.fight = f.text; commit.fight = f.sig end
    if f.turn.mine and (full or f.turnSig ~= s.turnSig) then
        local list = can(f.turn.mine, limits)
        if #list > 0 then raw.can = list end
    end
    commit.turnSig = f.turnSig

    local p = party()
    if full or not sameList(p, s.party or {}) then raw.party = p; commit.party = p end

    local waits = AI.Waiting(limits)
    if full or not sameList(waits.list, s.waiting or {}) then raw.waiting = waits.list; commit.waiting = waits.list end

    local around = near(limits, f.turn.on, options.wide)
    local fresh = {}
    for id, row in pairs(around) do
        local was = s.near[id]
        if full then fresh[#fresh + 1] = { d = row.d, text = row.text }
        elseif not was then fresh[#fresh + 1] = { d = row.d, text = "+ " .. row.text }
        elseif was ~= row.sig then fresh[#fresh + 1] = { d = row.d, text = "~ " .. row.text } end
    end
    table.sort(fresh, function(a, b) return a.d < b.d end)
    if #fresh > 0 then
        raw.near = {}
        for _, row in ipairs(fresh) do raw.near[#raw.near + 1] = row.text end
    end
    commit.near = {}
    for id, row in pairs(around) do commit.near[id] = row.sig end

    local lines, heardLast = heard(s.heard, waits.spoken)
    if #lines > 0 then raw.heard = lines end
    commit.heard = heardLast

    local rows, seq, rolls = events(s.seq)
    if #rows > 0 then raw.events = rows end
    if #rolls > 0 then raw.rolls = rolls end
    commit.seq = seq

    local scenesNow = AI.Mode.Part("scenes") and scenes() or {}
    local changed = {}
    for _, name in ipairs(sortedNames(scenesNow)) do
        if full or s.scenes[name] ~= scenesNow[name] then changed[#changed + 1] = name .. ": " .. scenesNow[name] end
    end
    for _, name in ipairs(sortedNames(s.scenes)) do
        if scenesNow[name] == nil and AI.Mode.Part("scenes") then changed[#changed + 1] = name .. ": gone" end
    end
    if #changed > 0 then raw.scenes = changed end
    commit.scenes = scenesNow

    local route = AI.Mode.Part("route") and AI.Plan and AI.Plan.Line and AI.Plan.Line() or nil
    if (full or route ~= s.route) and route then raw.route = route end
    commit.route = route

    local told, notes = s.notes or {}, {}
    commit.notes = {}
    for _, note in ipairs(AI.Notes and AI.Notes.Near(false) or {}) do
        commit.notes[note.n] = true
        if full or not told[note.n] then notes[#notes + 1] = note.text end
    end
    if #notes > 0 then raw.notes = notes end

    -- shaped to the sizes: lists cut from their old end with a count. While the block is still
    -- over the hard cap, what the agent acts on (waiting, can, party) is kept whole and the rest
    -- gives way first: rolls, then what is around, heard and happened; only then the texts and
    -- the lists that are acted on
    local now, text
    local L = limits.list
    local half = math.max(1, math.ceil(L / 2))
    local STAGES = {
        { rolls = L, soft = L, hard = L, text = 1 },
        { rolls = 2, soft = half, hard = L, text = 1 },
        { rolls = 0, soft = 2, hard = L, text = 1 },
        { rolls = 0, soft = 1, hard = L, text = 0.8 },
        { rolls = 0, soft = 0, hard = L, text = 0.7 },
        { rolls = 0, soft = 0, hard = half, text = 0.5 },
    }
    local HARD = { can = true, party = true, waiting = true }
    for _, stage in ipairs(STAGES) do
        local textLimit = math.max(40, math.floor(limits.text * stage.text))
        now = { where = raw.where, fight = raw.fight, route = raw.route and cut(raw.route, textLimit * 2) or nil }
        local more = {}
        for _, key in ipairs({ "can", "party", "waiting", "near", "heard", "events", "rolls", "scenes", "notes" }) do
            if raw[key] then
                local n = key == "rolls" and stage.rolls or HARD[key] and stage.hard or stage.soft
                local left
                if key == "near" then
                    -- the nearest are kept
                    now.near = {}
                    for i = 1, math.min(n, #raw.near) do now.near[i] = cut(raw.near[i], textLimit) end
                    left = #raw.near - #now.near
                elseif key == "waiting" then
                    -- what waits is the thing to act on: its texts keep their own length
                    now.waiting, left = lastOf(raw.waiting, math.max(n, 3), limits.text * 6)
                else
                    now[key], left = lastOf(raw[key], n, textLimit)
                end
                if left > 0 then
                    more[key] = left
                    if #now[key] == 0 then now[key] = nil end
                end
            end
        end
        if next(more) ~= nil then now.more = more end
        text = encode(now, KEYS)
        if #text <= limits.chars then break end
    end

    if options.part then
        local only = { [options.part] = now[options.part] }
        now, text = only, encode(only, KEYS)
    end
    if not options.peek then
        for k, v in pairs(commit) do s[k] = v end
        s.fight = commit.fight or s.fight
        s.first = nil
    end
    return now, #text
end
