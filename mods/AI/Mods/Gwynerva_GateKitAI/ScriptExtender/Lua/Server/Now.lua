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

-- chars: the hard cap of a `now` block as JSON text; list: the longest list; text: the longest text;
-- line: the longest line of actions in `can` (Server/Turn.lua); ways: the radius of `ways` (a way out
-- is worth knowing farther off than a creature); can: the characters a fight adds to the cap for
-- `can`, so that a turn's set is not cut for the rest of the block.
AI.Detail = AI.Detail or {
    brief = { chars = 1200, list = 6, text = 120, radius = 25, ways = 40, line = 480, can = 2500 },
    full = { chars = 6000, list = 30, text = 300, radius = 40, ways = 60, line = 6000, can = 2500 },
}
AI.Limit = AI.Limit or AI.Detail.brief
AI.Sessions = AI.Sessions or {}

-- the order of `now` and the JSON text: Shared/Encode.lua
local KEYS = AI.NowKeys
local encode = AI.EncodeWith

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
    -- who is controlled: told again when it changes (a death, a fight's end); nobody, when the
    -- game controls no member
    local who = try(Osi.IsControlled, me) == 1 and string.format("%s (controlled)", tostring(nameOf(me) or "?"))
        or string.format("nobody controlled (do=select who=<name>), %s", tostring(nameOf(me) or "?"))
    return { text = string.format("%s%s; %s at %s", tostring(place.level or "?"), by, who, at), level = place.level, p = p, who = who }
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
    -- the member the acts go to, said outright: the controlled one may be another (a downed one)
    if t.mine then text = text .. string.format("; acts without who are %s's", tostring(nameOf(t.mine))) end
    return { text = text, sig = text, turn = t, turnSig = tostring(t.round) .. "|" .. table.concat(ids, ",") }
end

-- "4 m NE" from `from` to `to` (north = +z, east = +x), whole metres
local function bearing(from, to)
    local dx, dz = to[1] - from[1], to[3] - from[3]
    local d = math.sqrt(dx * dx + dz * dz)
    local names = { "E", "NE", "N", "NW", "W", "SW", "S", "SE" }
    local k = math.floor((math.atan(dz, dx) / (math.pi / 4)) + 0.5) % 8
    return string.format("%d m %s", math.floor(d + 0.5), d < 0.5 and "here" or names[k + 1])
end

local function party()
    local out = {}
    -- in a fight each other member's place from the one whose turn is open (or the controlled one):
    -- an area spell is aimed knowing where the party stands
    local t = AI.Turn()
    local centre = t.on and U.posOf(t.mine or U.controlled()) or nil
    local centreId = t.on and (t.mine or U.controlled()) or nil
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
        -- downed is the game's status; a member made immortal (a staged scene) stands at 0
        local state = m.dead and ", dead" or (try(Osi.HasActiveStatus, m.id, "DOWNED") == 1 and ", downed")
            or (m.hp == 0 and try(Osi.IsImmortal, m.id) == 1 and ", stands at 0 (immortal)") or ""
        local place = ""
        if centre and (uuidOf(m.id) or m.id) ~= (uuidOf(centreId) or centreId) then
            local p = U.posOf(m.id)
            if p then place = ", " .. bearing(centre, p) end
        end
        out[#out + 1] = string.format("%s %s/%s hp%s%s%s%s", tostring(m.name), tostring(m.hp), tostring(m.maxHp), state, place,
            #hands > 0 and (", holds " .. table.concat(hands, " + ")) or "",
            #conditions > 0 and (", " .. table.concat(conditions, ", ")) or "")
    end
    -- the same order whoever is controlled: a change of control is not a change of the party
    table.sort(out)
    return out
end

local NOTABLE = { door = true, ladder = true, portal = true, usable = true, container = true }

-- Who and what of note the party perceives: id -> { sig, text, d }. What `ways` lists (`skip`)
-- is not said twice. Blind: what a member's sight holds (AI.Ways.SeeOptions); distances and
-- bearings from the controlled character.
local function near(limits, inFight, wide, skip)
    local out = {}
    -- a step's block lists what the party perceives; a look in open mode, everything within the radius
    local options = AI.Ways.SeeOptions(limits.radius, false, false)
    options.scope = wide and AI.Mode.Scope(false) or "seen"
    local around = try(GK.See.Around, options) or {}
    local from = U.posOf(U.controlled())
    for _, c in ipairs(around.characters or {}) do
        if c.rel ~= "party" and not c.remembered then
            local id = uuidOf(c.id) or c.id
            local state = c.dead and "dead" or c.downed and "down" or c.perceived == false and "unseen" or nil
            -- hit points as the health bar shows them: none when the game hides an NPC's (AI.HideNpcHp)
            local hp = (c.rel == "hostile" and inFight and c.hp and not AI.HideNpcHp()) and (tostring(c.hp) .. " hp") or nil
            local does = c.dead and c.loot and "loot" or c.talk and not c.dead and c.rel ~= "hostile" and "talk" or nil
            local words = { tostring(c.rel or "?") }
            -- the boss frame a player sees on its health bar (TargetHealthBarTemplate: CharacterType Boss)
            if try(Osi.IsBoss, id) == 1 then words[#words + 1] = "boss" end
            if state then words[#words + 1] = state end
            if hp then words[#words + 1] = hp end
            if does then words[#words + 1] = "can " .. does end
            -- the agent's own notes of this one (AI.Notes with `about`): their count and the last
            local head = string.format("%s %s %s%s", tostring(c.name or "someone"), handle(id), table.concat(words, ", "), AI.Notes.Mark(id))
            local dy = (from and c.pos) and (c.pos[2] - from[2]) or nil
            out[id] = { sig = head, text = string.format("%s, %s m %s%s", head, tostring(c.d), tostring(c.dir or ""), AI.Ways.Height(dy)), d = c.d or 0 }
        end
    end
    for _, o in ipairs(around.objects or {}) do
        if NOTABLE[o.kind] and not o.remembered and not (skip and skip[uuidOf(o.id) or o.id]) then
            local id = uuidOf(o.id) or o.id
            local words = { tostring(o.kind) }
            if o.locked then words[#words + 1] = "locked" end
            if o.open then words[#words + 1] = "open" end
            if o.trap then words[#words + 1] = "trapped" end
            local head = string.format("%s %s (%s)", tostring(o.name or "something"), handle(id), table.concat(words, ", "))
            out[id] = { sig = head, text = string.format("%s, %s m %s%s", head, tostring(o.d), tostring(o.dir or ""), AI.Ways.Height(o.dy)), d = o.d or 0 }
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
--- busy = why the game goes on by itself (a line being spoken, a film, a load), kinds, widgets =
--- the names of the widgets that wait }.
function AI.Waiting(limits)
    limits = limits or AI.Limit
    local items = {}
    for _, w in pairs(GK.Attention.Waiting) do items[#items + 1] = w end
    for _, w in ipairs(AI.Client.open or {}) do items[#items + 1] = w end
    -- a film the client sees playing (GK.Gui.Stuck), when the log holds none open
    if AI.Client.stuck == "film" then
        local told = false
        for _, w in ipairs(items) do if (w.what or w.kind) == "film" then told = true end end
        if not told then items[#items + 1] = { kind = "film", seq = math.huge } end
    end
    table.sort(items, function(a, b) return (a.seq or 0) < (b.seq or 0) end)
    local out = { list = {}, kinds = {} }
    local phase
    for _, w in ipairs(items) do
        local kind = w.what or w.kind
        if PHASES[kind] then phase = w end
        out.kinds[kind or "?"] = true
        -- the widgets of what waits: a press is taken there only, while it waits
        if type(w.widget) == "string" then out.widgets = out.widgets or {}; out.widgets[w.widget] = true end
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
        elseif kind == "film" then
            text = "a film plays -> do=close skips it, do=wait watches it"
            out.actionable = true
        elseif kind == "loading" or kind == "saving" then
            out.busy = "the game is " .. kind
        elseif kind and not QUIET[kind] then
            local detail = ""
            if type(blocking) == "table" and blocking.window == (w.window or kind) and (blocking.text or blocking.answers) then
                detail = blocking.text and (": " .. cut(blocking.text, limits.text)) or ":"
                if blocking.answers then detail = detail .. " " .. table.concat(answersOf(blocking.answers, limits), " ") end
            end
            text = string.format("a %s window%s -> %s", tostring(w.window or kind), detail, HOW[kind] or "do=close")
            out.actionable = true
        end
        if text then out.list[#out.list + 1] = text end
    end
    if not out.actionable and not out.busy and U.inDialogue() then out.busy = "a dialogue is on" end
    -- the windows that are not the HUD's: used with do=ui; they do not hold the other acts back
    local windows = AI.Client.windows or {}
    if #windows > 0 then
        table.insert(out.list, 1, "open windows: " .. table.concat(windows, ", ") .. " -> do=ui window=<one of them> lists its controls")
    end
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
---   where    the level, the nearest named thing, the controlled character (or that nobody is) and
---            its position; again after two metres or more, or when control changes
---   fight    "off", or the round and whose turn is open (`(mine)`: a party member's)
---   countdown  the story's timers: their text, the rounds left, the thing the text names (AI.Countdown)
---   can      in a party member's open turn: what is left and every action available now
---            (AI.Can); again whenever any of it changed
---   party    each member: hit points, what is in hand, conditions; when any of it changed
---   waiting  what the game waits for from the player, each with the step that answers it; an
---            empty list when what waited is gone
---   ways     the passages out of here the agent may know of (AI.Ways): ladders, doors, portals
---            with where they lead, then the mechanisms worked in place; `+` new, `~` changed
---   near     creatures and usable things of note the party perceives (blind: in a member's
---            sight), but for what `ways` lists: `+` new, `~` changed; each with its handle, how
---            it stands to the party, distance and bearing from the controlled character, and
---            the height when it is above or below
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
    if full or w.level ~= s.level or w.who ~= s.who or not (w.p and s.p) or U.dist(w.p, s.p) >= 2 then
        raw.where = w.text
        commit.level, commit.p, commit.who = w.level, w.p, w.who
    end

    local f = fight()
    if full or f.sig ~= s.fight then raw.fight = f.text; commit.fight = f.sig end
    -- the agent's own goals (Server/Goals.lua): told when they change, and again when a party
    -- member's turn opens - then shown over that member's head as well
    local goals = AI.GoalLines and AI.GoalLines() or {}
    local goalsSig = table.concat(goals, "|")
    local turnOpened = f.turn.mine and (f.turnSig or "") ~= (s.goalsTurn or "")
    if #goals > 0 and (full or goalsSig ~= s.goals or turnOpened) then raw.goals = goals end
    commit.goals, commit.goalsTurn = goalsSig, f.turnSig
    if turnOpened and #goals > 0 and not options.peek and goalsSig == s.goals then pcall(AI.ShowGoals, f.turn.mine) end
    -- the open turn's resources and actions (Server/Turn.lua): told again whenever any of it changed
    local canNow = f.turn.mine and AI.Can(f.turn.mine, limits) or {}
    local canSig = table.concat(canNow, "\n")
    if #canNow > 0 and (full or canSig ~= s.can) then raw.can = canNow end
    commit.can = canSig
    commit.turnSig = f.turnSig

    local countdown = AI.Countdown()
    local countdownSig = table.concat(countdown, "\n")
    if #countdown > 0 and (full or countdownSig ~= s.countdown) then raw.countdown = countdown end
    if #countdown == 0 and (s.countdown or "") ~= "" then raw.countdown = { "over" } end
    commit.countdown = countdownSig

    local p = party()
    if full or not sameList(p, s.party or {}) then raw.party = p; commit.party = p end

    local waits = AI.Waiting(limits)
    if full or not sameList(waits.list, s.waiting or {}) then raw.waiting = waits.list; commit.waiting = waits.list end

    -- a list told as a delta: `+` new, `~` changed; what is in sight now before what was seen
    -- before, the nearest first, a mechanism as if 15 m farther than a passage at its distance
    local function delta(key, rows)
        local was, fresh = s[key] or {}, {}
        for id, row in pairs(rows) do
            local rank = (row.remembered and 1e6 or 0) + (row.class == "mechanism" and 15 or 0) + (row.d or 0)
            if full then fresh[#fresh + 1] = { d = rank, text = row.text }
            elseif not was[id] then fresh[#fresh + 1] = { d = rank, text = "+ " .. row.text }
            elseif was[id] ~= row.sig then fresh[#fresh + 1] = { d = rank, text = "~ " .. row.text } end
        end
        table.sort(fresh, function(a, b) return a.d < b.d end)
        if #fresh > 0 then
            raw[key] = {}
            for _, row in ipairs(fresh) do raw[key][#raw[key] + 1] = row.text end
        end
        commit[key] = {}
        for id, row in pairs(rows) do commit[key][id] = row.sig end
    end
    local ways = AI.Ways.Rows(limits, options.wide)
    delta("ways", ways)
    delta("near", near(limits, f.turn.on, options.wide, ways))

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
    -- a turn's set has its own room on top of the cap
    local cap = limits.chars + (raw.can and (limits.can or 0) or 0)
    for _, stage in ipairs(STAGES) do
        local textLimit = math.max(40, math.floor(limits.text * stage.text))
        now = { where = raw.where, fight = raw.fight, countdown = raw.countdown, goals = raw.goals, route = raw.route and cut(raw.route, textLimit * 2) or nil }
        local more = {}
        for _, key in ipairs({ "can", "party", "waiting", "ways", "near", "heard", "events", "rolls", "scenes", "notes" }) do
            if raw[key] then
                local n = key == "rolls" and stage.rolls or HARD[key] and stage.hard or stage.soft
                local left
                if key == "near" or key == "ways" then
                    -- the nearest are kept
                    now[key] = {}
                    for i = 1, math.min(n, #raw[key]) do now[key][i] = cut(raw[key][i], textLimit) end
                    left = #raw[key] - #now[key]
                elseif key == "can" then
                    -- the turn's whole set is what the agent decides on: never shortened here
                    -- (AI.Can keeps each line within its own length)
                    now.can, left = raw.can, 0
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
        if #text <= cap then break end
    end
    -- the last resort: can's long lines cut, their count in `more` (ai_look part=can reads them whole)
    local whole = now.can
    for _, width in ipairs({ limits.text * 2, limits.text }) do
        if #text <= cap or not whole then break end
        local cutLines, shortened = 0, {}
        for i, line in ipairs(whole) do
            shortened[i] = cut(line, width)
            if shortened[i] ~= line then cutLines = cutLines + 1 end
        end
        if cutLines > 0 then
            now.can = shortened
            now.more = now.more or {}
            now.more.can = cutLines
            text = encode(now, KEYS)
        end
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
