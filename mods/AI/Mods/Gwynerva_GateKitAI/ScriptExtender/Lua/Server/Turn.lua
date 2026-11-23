-- AI.Can, AI.Left, AI.Countdown, AI.Thoughts (server side): a turn in a fight as the game counts
-- it - what is left and every action available now - the story's countdowns, and the agent's
-- reasons. Nothing is ranked or advised: the set is given, the agent decides.
--
--   AI.Can(actor, limits)   the lines of `now.can` for the member whose turn is open
--   AI.Left(actor)          what is left of its turn: "action 1, bonus 1, reaction 1, movement 9 m; ..."
--   AI.Countdown()          the lines of `now.countdown`: what each story timer says, the rounds left,
--                           and the thing its text names, when the mode lets the agent know it
--   AI.Thoughts(n)          the last `n` reasons given with steps (`think`), the newest last
--   AI.Think                data: { required = false }; with `required` a step without `think` is refused
--
-- Read from GateKit: GK.See.Resources (the ActionResources component), GK.See.Actions (the spell
-- book, each action's UseCosts and range, ready or not), GK.See.Odds (the tooltip's chance),
-- GK.Conditions (the game's own target conditions), CombatIsThreatened (who threatens whom),
-- GK.See.Timers (the StoryTimer entities).

local GK = AI.GK
local U = AI.Util
local try, uuidOf, nameOf, handle, cut, round1 = U.try, U.uuidOf, U.nameOf, U.handle, U.cut, U.round1

AI.Think = AI.Think or { required = false }

local function num(v)
    v = round1(tonumber(v) or 0)
    return v == math.floor(v) and string.format("%d", v) or tostring(v)
end

-- ---------------------------------------------------------------------------------------
-- What is left

--- What is left of `actor`'s turn as the game counts it, in words, and GK.See.Resources' table.
function AI.Left(actor)
    local r = try(GK.See.Resources, actor) or {}
    local parts = {}
    for _, kind in ipairs({ "action", "bonus", "reaction" }) do
        if r[kind] then parts[#parts + 1] = kind .. " " .. num(r[kind][1]) end
    end
    if r.movement then parts[#parts + 1] = "movement " .. num(r.movement[1]) .. " m" end
    local extra = {}
    if r.slots then
        local levels = {}
        for level in pairs(r.slots) do levels[#levels + 1] = level end
        table.sort(levels)
        local slots = {}
        for _, level in ipairs(levels) do
            slots[#slots + 1] = string.format("%d:%s/%s", level, num(r.slots[level][1]), num(r.slots[level][2]))
        end
        extra[#extra + 1] = "slots " .. table.concat(slots, " ")
    end
    local other = {}
    for name, pair in pairs(r.other or {}) do
        if (tonumber(pair[2]) or 0) > 0 then other[#other + 1] = string.format("%s %s/%s", name, num(pair[1]), num(pair[2])) end
    end
    table.sort(other)
    for _, line in ipairs(other) do extra[#extra + 1] = line end
    return table.concat(parts, ", ") .. (#extra > 0 and ("; " .. table.concat(extra, ", ")) or ""), r
end

-- ---------------------------------------------------------------------------------------
-- Enemies, threats, actions

-- The hostiles in the fight the party perceives, within 40 m of `actor`, the nearest first.
local function enemies(actor)
    local out = {}
    local mp = U.posOf(actor)
    if not mp then return out end
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter")) do
        local id = try(function() return e.Uuid.EntityUuid end)
        local p = id and U.posOf(id)
        if p and U.dist(p, mp) <= 40 and try(Osi.IsInCombat, id) == 1 and try(Osi.IsDead, id) ~= 1
            and try(GK.See.Relation, id) == "hostile" and try(GK.See.PartyPerceives, id) then
            out[#out + 1] = { id = id, name = nameOf(id), d = U.dist(p, mp), hp = try(function() return e.Health.Hp end) }
        end
    end
    table.sort(out, function(a, b) return a.d < b.d end)
    return out
end

local function disengaged(actor)
    local e = try(Ext.Entity.Get, actor)
    for _, id in pairs(e and try(function() return e.StatusContainer.Statuses end) or {}) do
        if tostring(id):find("DISENGAGE") then return true end
    end
    return false
end

--- Who would make an opportunity attack if `actor` left its reach now: the game's own list of
--- those threatening it (CombatIsThreatened.ThreatenedBy), hostile, alive, with a reaction left;
--- nobody once it has disengaged.
local function threats(actor)
    local out = {}
    if disengaged(actor) then return out end
    local e = try(Ext.Entity.Get, actor)
    for _, h in ipairs(e and try(function() return e.CombatIsThreatened.ThreatenedBy end) or {}) do
        local id = try(function() return h.Uuid.EntityUuid end)
        if id and try(GK.See.Relation, id) == "hostile" and try(Osi.IsDead, id) ~= 1
            and ((try(GK.See.Resources, id) or {}).reaction or { 1 })[1] > 0 then
            out[#out + 1] = { name = tostring(nameOf(id)), id = id }
        end
    end
    -- a name written once for a run of it: "Human #a1b2c3, #d4e5f6"
    table.sort(out, function(a, b) return a.name < b.name end)
    local previous
    for i, row in ipairs(out) do
        out[i] = (previous == row.name and "" or (row.name .. " ")) .. handle(row.id)
        previous = row.name
    end
    return out
end
AI.Threats = threats

local AIMED = { Target = true, Projectile = true, Zone = true, Cone = true, Throw = true }
local BASE = { action = true, bonus = true, reaction = true, movement = true, slot = true }

-- The group of an action by what it spends: action, bonus, or free (movement or nothing).
local function groupOf(costs)
    if costs.action then return "action" end
    if costs.bonus then return "bonus" end
    if costs.reaction then return nil end
    return "free"
end

-- What an action costs besides its group: a spell slot's level, a class resource, movement.
local function extraCost(costs)
    local parts = {}
    if costs.slot then parts[#parts + 1] = "slot " .. costs.slot end
    if costs.movement then parts[#parts + 1] = "movement " .. num(costs.movement) end
    local names = {}
    for name, n in pairs(costs) do
        if not BASE[name] then names[#names + 1] = name .. " " .. num(n) end
    end
    table.sort(names)
    for _, n in ipairs(names) do parts[#parts + 1] = n end
    return #parts > 0 and (" (" .. table.concat(parts, ", ") .. ")") or ""
end

-- One action's entry: what follows its id - range, extra cost, and the enemies it reaches with the
-- game's chance: "#a1b2c3 75%" in reach now, "#a1b2c3 75% +4.5 m" after a walk the movement left
-- covers - and whether it is aimed at a creature with none to aim at now.
local function entry(actor, a, foes, movement, list)
    local text = ""
    if type(a.range) == "number" and a.range > 0 then text = " " .. num(a.range) .. " m" end
    text = text .. extraCost(a.costs or {})
    if not (AIMED[a.type] and type(a.range) == "number" and a.range > 0) then return text, false end
    local reach, aimed = {}, false
    for _, f in ipairs(foes) do
        local move = math.max(0, f.d - a.range - 0.6)
        local check = GK.Conditions and try(GK.Conditions.Check, actor, a.id, f.id) or {}
        local allowed = check.ok ~= false
        local odds = try(GK.See.Odds, actor, a.id, f.id) or {}
        if odds.kind and odds.kind ~= "none" then aimed = true end
        if allowed and odds.kind ~= "none" and move <= movement + 0.01 then
            reach[#reach + 1] = { id = handle(f.id), move = move,
                chance = odds.chance and string.format("%d%%", math.floor(odds.chance * 100 + 0.5)) or "?" }
        end
    end
    -- an action at a point (a jump), not at a creature: no enemy to list
    if not aimed then return text, false end
    if #reach == 0 then return text, true end
    list = list or #reach
    local function handles(rows)
        local ids = {}
        for i = 1, math.min(#rows, list) do ids[#ids + 1] = rows[i].id end
        return table.concat(ids, ", ") .. (#rows > list and string.format(" (+%d)", #rows - list) or "")
    end
    local same = true
    for _, r in ipairs(reach) do if r.chance ~= reach[1].chance then same = false end end
    if same then
        -- the chance once; the reach as the enemies in it now, or the walk to the nearest
        -- (the others' distances are the enemies line's)
        local now = {}
        for _, r in ipairs(reach) do if r.move <= 0.05 then now[#now + 1] = r end end
        local where
        if #now == #foes and #foes > 1 then where = "all in reach"
        elseif #now > 0 then where = "in reach " .. handles(now)
        else where = string.format("nearest %s +%s m", reach[1].id, num(reach[1].move)) end
        return text .. ": " .. reach[1].chance .. ", " .. where, false
    end
    -- chances that differ (another armour, cover): each enemy with its own, the nearest first
    local parts = {}
    for i = 1, math.min(#reach, list) do
        local r = reach[i]
        parts[#parts + 1] = r.id .. " " .. r.chance .. (r.move > 0.05 and (" +" .. num(r.move) .. " m") or "")
    end
    return text .. ": " .. table.concat(parts, ", ") .. (#reach > list and string.format(" (+%d)", #reach - list) or ""), false
end

-- Names of one entry with the variants of an action written once: ids sharing all but their
-- last part ("Projectile_ChromaticOrb_Acid", "..._Cold") as "Projectile_ChromaticOrb_{Acid,Cold}".
--- Whether the game hides the hit points of creatures the player does not control: the custom
--- difficulty's "Hide NPC health", the ruleset modifier HIDE_NPC_HP (RulesetModifiers, keyed by
--- the modifier's uuid from the static data), which the health bars' XAML follows.
function AI.HideNpcHp()
    if AI.HideNpcHpId == nil then
        AI.HideNpcHpId = false
        for _, id in ipairs(try(Ext.StaticData.GetAll, "RulesetModifier") or {}) do
            local m = try(Ext.StaticData.Get, id, "RulesetModifier")
            if m and tostring(m.Name) == "HIDE_NPC_HP" then AI.HideNpcHpId = id end
        end
    end
    if not AI.HideNpcHpId then return false end
    local e = (try(Ext.Entity.GetAllEntitiesWithComponent, "RulesetModifiers") or {})[1]
    local mods = e and try(function() return Ext.Types.Serialize(e.RulesetModifiers).Modifiers end)
    return type(mods) == "table" and mods[AI.HideNpcHpId] == true
end

--- A creature's race as the game names it to a player (its Race's display name), or nil.
function AI.RaceOf(id)
    local e = try(Ext.Entity.Get, id)
    local race = e and try(function() return e.Race.Race end)
    local r = race and try(Ext.StaticData.Get, race, "Race")
    local name = r and try(function() return Ext.Loca.GetTranslatedString(r.DisplayName.Handle.Handle) end)
    return name ~= "" and name or nil
end

AI.VariantsFold = AI.VariantsFold or 4
local function variants(names, fold)
    -- many variants of one action of two parts ("Shout_DisguiseSelf_Dwarf_Female", ... thirty of
    -- them) are one entry with their count; the full look names them all
    local byTwo, counted = {}, {}
    for _, name in ipairs(names) do
        local two = name:match("^([^_]+_[^_]+)_")
        if two then byTwo[two] = (byTwo[two] or 0) + 1 end
    end
    local kept = {}
    for _, name in ipairs(names) do
        local two = name:match("^([^_]+_[^_]+)_")
        if two and byTwo[two] >= AI.VariantsFold then
            if not counted[two] then
                counted[two] = true
                kept[#kept + 1] = string.format("%s_* (%d kinds: ai_look part=can detail=full)", two, byTwo[two])
            end
        else
            kept[#kept + 1] = name
        end
    end
    if fold then names = kept end
    local order, by = {}, {}
    for _, name in ipairs(names) do
        local stem, last = name:match("^(.+)_([^_]+)$")
        -- only a stem of two parts or more: "Shout_Dash" stays whole
        if not (stem and stem:find("_", 1, true)) then stem, last = name, nil end
        if not by[stem] then by[stem] = {}; order[#order + 1] = stem end
        by[stem][#by[stem] + 1] = { name = name, last = last }
    end
    local out = {}
    for _, stem in ipairs(order) do
        local rows = by[stem]
        if #rows > 1 then
            local lasts = {}
            for _, r in ipairs(rows) do lasts[#lasts + 1] = r.last end
            out[#out + 1] = stem .. "_{" .. table.concat(lasts, ",") .. "}"
        else
            out[#out + 1] = rows[1].name
        end
    end
    return table.concat(out, ", ")
end

--- The action groups of `actor`, each one line: "action: <entry> | <entry>", "bonus: ...",
--- "free: ...", every ready action of its spell book in the book's order (level, then id); actions
--- with the same range, cost and reach share one entry ("Shout_Dash, Shout_Hide"). Last, "no
--- target now: ...": the actions aimed at a creature that reach no enemy now, out of range or
--- refused by the game's target conditions (a sneak attack without advantage), and "too little
--- movement: ...": those whose cost in movement is more than is left.
function AI.ActionLines(actor, foes, movement, chars, list)
    local listSize = list
    local groups, order, none, short = {}, { "action", "bonus", "free" }, {}, {}
    for _, a in ipairs(try(GK.See.Actions, actor) or {}) do
        local group = a.ready and groupOf(a.costs or {})
        local needs = group and tonumber((a.costs or {}).movement)
        if needs and needs > movement + 0.01 then
            short[#short + 1] = tostring(a.id) .. " (" .. num(needs) .. " m)"
        elseif group then
            local tail, idle = entry(actor, a, foes, movement, list)
            if idle then
                none[#none + 1] = tostring(a.id)
            else
                local g = groups[group] or { list = {}, at = {} }
                groups[group] = g
                local same = g.at[tail]
                if same then same.names[#same.names + 1] = tostring(a.id)
                else
                    same = { names = { tostring(a.id) }, tail = tail }
                    g.at[tail] = same
                    g.list[#g.list + 1] = same
                end
            end
        end
    end
    local out = {}
    for _, group in ipairs(order) do
        -- those that name their targets first, then those that cost nothing more (Dash,
        -- Disengage), then the rest: a line too long loses the last
        local list, free, costly = {}, {}, {}
        for _, e in ipairs(groups[group] and groups[group].list or {}) do
            local text = variants(e.names, (listSize or 0) < AI.Detail.full.list) .. e.tail
            if e.tail:find(": ", 1, true) then list[#list + 1] = text
            elseif e.tail == "" then free[#free + 1] = text
            else costly[#costly + 1] = text end
        end
        for _, text in ipairs(free) do list[#list + 1] = text end
        for _, text in ipairs(costly) do list[#list + 1] = text end
        if #list > 0 then
            local line = group .. ": " .. table.concat(list, " | ")
            if #line > chars then
                local kept, n = group .. ":", 0
                for i, e in ipairs(list) do
                    local more = string.format(" (+%d more: ai_look part=can detail=full)", #list - i + 1)
                    if #kept + #e + 3 + #more > chars then kept = kept .. more; break end
                    kept = kept .. (n > 0 and " | " or " ") .. e
                    n = n + 1
                end
                line = kept
            end
            out[#out + 1] = line
        end
    end
    if #none > 0 then out[#out + 1] = cut("no target now: " .. table.concat(none, ", "), chars) end
    if #short > 0 then out[#out + 1] = cut("too little movement: " .. table.concat(short, ", "), chars) end
    return out
end

--- The lines of `now.can` for `actor`, whose turn is open: who and what is left; the opportunity
--- attacks leaving its place would provoke; the enemies it can see with their distance and hit
--- points; one line per group of actions (action, bonus, free), each action with its range, its
--- cost beyond the group, and the enemies it reaches now or after a walk the movement left covers,
--- with the game's chance; the other party members whose turn is open too, with what they have left.
function AI.Can(actor, limits)
    limits = limits or AI.Limit
    local out = {}
    local left, res = AI.Left(actor)
    out[1] = string.format("%s: %s", tostring(nameOf(actor)), left)
    local provoke = threats(actor)
    if #provoke > 0 then out[#out + 1] = "leaving the reach of " .. table.concat(provoke, ", ") .. " provokes an opportunity attack" end
    local foes = enemies(actor)
    if #foes > 0 then
        -- the nearest `list`; a name written once for a run of that name, distance and hit points
        -- once for a run of equal ones: "Imp #a1b2c3, #d4e5f6 9 m 6 hp"
        local list, shown = {}, math.min(#foes, limits.list)
        local hideHp = AI.HideNpcHp()
        local function tailOf(f) return string.format(" %s m%s", num(f.d), (f.hp and not hideHp) and (" " .. tostring(f.hp) .. " hp") or "") end
        for i = 1, shown do
            local f, before, after = foes[i], foes[i - 1], i < shown and foes[i + 1] or nil
            -- with its name: its race as a player sees it (the game's Race, by its display name),
            -- and what an examine showed of this kind of creature (AI.Examined)
            local about = {}
            local race = AI.RaceOf(f.id)
            if race and not tostring(f.name):find(race, 1, true) then about[#about + 1] = race end
            -- the boss frame of the target's health bar (TargetHealthBarTemplate: CharacterType Boss)
            if try(Osi.IsBoss, f.id) == 1 then about[#about + 1] = "boss" end
            if AI.Examined and AI.Examined[tostring(f.name)] then about[#about + 1] = AI.Examined[tostring(f.name)] end
            local name = (before and before.name == f.name) and "" or (tostring(f.name) .. (#about > 0 and (" (" .. table.concat(about, ", ") .. ")") or "") .. " ")
            local tail = (after and after.name == f.name and tailOf(after) == tailOf(f)) and "" or tailOf(f)
            list[#list + 1] = name .. handle(f.id) .. AI.Notes.Mark(f.id) .. tail
        end
        out[#out + 1] = "enemies: " .. table.concat(list, ", ") .. (#foes > #list and string.format(" (+%d farther)", #foes - #list) or "")
    end
    for _, line in ipairs(AI.ActionLines(actor, foes, (res.movement or { 0 })[1], limits.line or limits.text * 4, limits.list)) do out[#out + 1] = line end
    local turn = AI.Turn()
    for _, row in ipairs(turn.turn or {}) do
        if row.mine and row.id ~= (uuidOf(actor) or actor) then
            out[#out + 1] = string.format("also open: %s (%s)", tostring(row.name), (AI.Left(row.id)))
        end
    end
    return out
end

--- A refusal of an act in `actor`'s open turn told with what is left and what still fits:
--- (why, hint).
function AI.TurnRefusal(actor, why)
    local left, res = AI.Left(actor)
    local fits = {}
    for _, line in ipairs(AI.ActionLines(actor, enemies(actor), (res.movement or { 0 })[1], AI.Limit.text * 2, AI.Limit.list)) do
        if not (line:find("^no target now") or line:find("^too little movement")) then fits[#fits + 1] = line end
    end
    return tostring(why) .. " | left: " .. left,
        #fits > 0 and ("fits now: " .. table.concat(fits, "; ")) or "nothing fits now: do=end"
end

-- ---------------------------------------------------------------------------------------
-- The countdown

-- Text folded to lower case for a match: ASCII and Cyrillic (the game's languages with cases in
-- one or two bytes of UTF-8 that a name and a sentence differ by).
local function fold(text)
    text = tostring(text or ""):lower()
    text = text:gsub("\208([\144-\159])", function(c) return "\208" .. string.char(c:byte() + 32) end)
    text = text:gsub("\208([\160-\175])", function(c) return "\209" .. string.char(c:byte() - 32) end)
    text = text:gsub("\208\129", "\209\145")
    return text
end
AI.Fold = fold

-- The thing a timer's text names, among what the mode lets the agent know (blind: seen now or
-- earlier on the level): the nearest whose name is in the text.
local function goalOf(text)
    local wanted = fold(text)
    local around = try(GK.See.Around, { radius = 90, limit = 120, scope = AI.Mode.Scope(true) }) or {}
    local best
    for _, list in ipairs({ around.objects or {}, around.characters or {} }) do
        for _, row in ipairs(list) do
            local name = row.name and fold(row.name)
            if name and #name >= 6 and wanted:find(name, 1, true) and not row.gone and (not best or (row.d or 0) < (best.d or 0)) then
                best = row
            end
        end
    end
    return best
end

--- The story's countdowns, one line each: "<the timer's own text>: <n> rounds left", and the
--- thing that text names with its distance and bearing when the agent may know it.
function AI.Countdown()
    local out = {}
    for _, t in ipairs(try(GK.See.Timers) or {}) do
        local text = t.text or t.name or "a countdown"
        local line = string.format("%s: %s rounds left", cut(text, 200), tostring(t.turnsLeft))
        local goal = goalOf(text)
        if goal then
            line = line .. string.format("; %s %s, %s m %s", tostring(goal.name), handle(goal.id), tostring(goal.d), tostring(goal.dir or ""))
        end
        out[#out + 1] = line
    end
    return out
end

-- ---------------------------------------------------------------------------------------
-- Thoughts

--- The last `n` reasons given with steps (the rows ai_step with `think`), the newest last:
--- "<who> <do> <target>: <think>", " (refused)" when the step was refused.
function AI.Thoughts(n)
    n = tonumber(n) or 6
    local all = {}
    for _, row in ipairs(GK.EventLog.Since(0, { kinds = { ai_step = true } }).rows or {}) do
        local p = row.payload or {}
        if type(p.think) == "string" and p.think ~= "" then
            all[#all + 1] = string.format("%s %s%s: %s%s", tostring(p.who or "?"), tostring(p["do"] or "?"),
                p.target and (" " .. tostring(p.target)) or "", p.think, p.ok == false and " (refused)" or "")
        end
    end
    local out = {}
    for i = math.max(1, #all - n + 1), #all do out[#out + 1] = all[i] end
    return out
end
