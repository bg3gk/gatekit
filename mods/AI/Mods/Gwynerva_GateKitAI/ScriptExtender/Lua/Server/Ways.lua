-- AI.Ways (server side): the ways out of where the agent stands, and why a walk does not get
-- somewhere.
--
--   AI.Ways.Rows(limits, wide)   the passages the agent may know of (GK.See.Passages): id -> { sig,
--                                text, d, class }; ladders, doors and portals first ("ordinary"),
--                                then the mechanisms worked in place (buttons, organs, pods)
--   AI.Ways.Line(row, from)      one passage as a line of `now.ways`
--   AI.Ways.WhyNot(actor, target, error)   for a walk that the pathfinder refuses: the reason and
--                                the call to make, or nil when walking does reach it
--
-- What may be known follows the mode (Mode.lua): blind, the passages in a member's sight now or
-- once (GK.See scope "known" with `sight`); open, every passage within the radius.

AI.Ways = AI.Ways or {}
local W = AI.Ways
local GK = AI.GK
local U = AI.Util
local try, uuidOf, handle, round1 = U.try, U.uuidOf, U.handle, U.round1

-- The options of a GK.See reader for what the agent may know: in blind mode what is in a
-- member's sight (now, or with `memory` also once); distances and bearings from the controlled
-- character.
W.View = W.View or 40
function W.SeeOptions(radius, memory, wide)
    local blind = AI.Mode.Blind()
    -- `view`: a door or a ladder in a clear line within 40 m is seen, as on the screen; the game's
    -- sight range (about 18 m) is its creatures' perception
    return { radius = radius, limit = 40, scope = (wide or memory) and AI.Mode.Scope(true) or AI.Mode.Scope(false),
             sight = blind or nil, view = blind and W.View or nil, from = U.controlled() }
end

-- "3.2 m above" / "4 m below", or "" on the same height.
function W.Height(dy)
    if not dy or math.abs(dy) < 1.5 then return "" end
    return string.format(", %s m %s", tostring(round1(math.abs(dy))), dy > 0 and "above" or "below")
end

function W.Line(row)
    local id = uuidOf(row.id) or row.id
    local words = { tostring(row.kind == "usable" and "mechanism" or row.kind) }
    if row.locked then words[#words + 1] = "locked" end
    if row.kind == "door" then words[#words + 1] = row.open and "open" or "closed" end
    local leads = ""
    if row.kind == "ladder" and row.foot and row.top then
        local to = row.way == "down" and row.foot or row.top
        leads = string.format(": %s to height %s (%s%s m)", row.way or "up", tostring(round1(to)), row.way == "down" and "-" or "+",
            tostring(round1(row.top - row.foot)))
    elseif row.kind == "portal" and row.to then
        leads = string.format(": to %s%s", tostring(row.to.name or (row.to.pos and table.concat(row.to.pos, ",")) or "?"),
            row.to.level and (" on " .. tostring(row.to.level)) or "")
    end
    local head = string.format("%s %s (%s)%s", tostring(row.name or "something"), handle(id), table.concat(words, ", "), leads)
    local seen = row.remembered and ", seen before" or ""
    return head, string.format("%s, %s m %s%s%s", head, tostring(row.d), tostring(row.dir or ""), W.Height(row.dy), seen)
end

function W.Rows(limits, wide)
    local out = {}
    local options = W.SeeOptions(math.max(limits.radius or 0, limits.ways or 0), true, wide)
    local got = try(GK.See.Passages, options) or {}
    for _, row in ipairs(got.passages or {}) do
        local id = uuidOf(row.id) or row.id
        local head, text = W.Line(row)
        out[id] = { sig = head, text = text, d = row.d or 0, class = row.class, remembered = row.remembered or nil }
    end
    return out
end

-- ---------------------------------------------------------------------------------------
-- Open ground: the ways out that are no thing at all (a corridor, an open deck)

local BEARINGS = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }

--- The bearings in which a walk from `actor` goes far over open ground the party sees (GateKit's
--- GK.See.Ways: the farthest point the pathfinder reaches by a path not much longer than the
--- straight line, on ground in a clear line from a member's eyes), kept from W.OpenMin (12) metres:
--- "W 28 m, NE 15 m", or nil. In open mode the ground need not be seen.
W.OpenMin, W.OpenRange = W.OpenMin or 12, W.OpenRange or 30
function W.Open(actor)
    local got = try(GK.See.Ways, { who = actor, range = W.OpenRange, all = not AI.Mode.Blind() }) or {}
    local out = {}
    for _, b in ipairs(BEARINGS) do
        local w = (got.ways or {})[b]
        if type(w) == "table" and (w.reach or 0) >= W.OpenMin then
            out[#out + 1] = string.format("%s %d m%s", b, math.floor(w.reach + 0.5), W.Height(w.dy))
        end
    end
    return #out > 0 and table.concat(out, ", ") or nil
end

-- ---------------------------------------------------------------------------------------
-- Why a walk does not get there

local NO_PATH = { "no path", "cannot get to it", "not walkable" }

local function refusedForPath(text)
    text = tostring(text or "")
    for _, p in ipairs(NO_PATH) do
        if text:find(p, 1, true) then return true end
    end
    return false
end

-- The nearest passage the agent knows of, a ladder that goes the needed way first.
local function nearestPassage(dy, except, actor)
    local got = try(GK.See.Passages, W.SeeOptions(60, true)) or {}
    local rows = {}
    for _, row in ipairs(got.passages or {}) do
        -- never the thing the refused walk was going to: that way is the one that failed
        if row.class == "ordinary" and (uuidOf(row.id) or row.id) ~= except then
            local score = row.d or 999
            if row.kind == "ladder" and dy and math.abs(dy) >= 1.5 and row.way == (dy > 0 and "up" or "down") then score = score - 1000 end
            rows[#rows + 1] = { row = row, score = score }
        end
    end
    table.sort(rows, function(a, b) return a.score < b.score end)
    -- the first that a walk from here reaches (the nearest few asked)
    for i = 1, math.min(5, #rows) do
        local p = rows[i].row.pos
        local path = (actor and p) and try(GK.See.Path, actor, p) or nil
        -- (a door stands in a wall: a walk ends a little before it)
        local within = rows[i].row.kind == "door" and 4.5 or 3
        if not path or (path.reachable and path.ends and U.dist(path.ends, p) < within) then return rows[i].row end
    end
    -- none checked is reached by a walk to its very spot (a door's centre in a wall, a ladder's
    -- foot): the nearest is still the way to try
    return rows[1] and rows[1].row or nil
end

--- When the pathfinder does not reach `target` (a uuid or {x,y,z}) from where `actor` stands:
--- why, in the game's own answers (GK.See.Path, GK.Act.Alternatives), and the call to make.
--- nil when the error is not about the path, or the walk does reach it.
function W.WhyNot(actor, target, err)
    if not refusedForPath(err) then return nil end
    local tp = type(target) == "table" and target or U.posOf(target)
    local mp = U.posOf(actor)
    if not (tp and mp) then return nil end
    local path = try(GK.See.Path, actor, tp) or {}
    -- reached: the walk ends within reach on the ground (an object on a raised spot - the helm's
    -- transmitter - is reached from beside its plinth, not at its centre)
    if path.ends then
        local e = path.ends
        local flat = math.sqrt((e[1] - tp[1]) ^ 2 + (e[3] - tp[3]) ^ 2)
        if flat < 2.5 and math.abs(e[2] - tp[2]) < 2.5 then return nil end
    end
    -- the actor itself may stand where no walk starts (on a cradle, a table, a corpse): then every
    -- target is "unreachable", and the remedy is a jump down, not another way
    if try(GK.Act.Stuck, actor) then
        local alt = try(GK.Act.Alternatives, actor, tp) or {}
        local down = alt.nearest and alt.nearest.pos
        return "no path: you stand where no walk can start (on top of something); jump down first",
            down and string.format('ai_step {"do":"jump","target":"%s,%s,%s"}', round1(down[1]), round1(down[2]), round1(down[3]))
                or 'ai_step {"do":"jump","target":"<a point x,y,z on the floor 2-3 m away>"}'
    end
    local dy = tp[2] - mp[2]
    local parts = {}
    if math.abs(dy) >= 2 then
        parts[#parts + 1] = string.format("it is %s m %s you, on another floor", tostring(round1(math.abs(dy))), dy > 0 and "above" or "below")
    else
        parts[#parts + 1] = "it is on your height, but no walk leads there from here: the way goes round"
    end
    local alt = try(GK.Act.Alternatives, actor, tp) or {}
    local jump
    for _, w in ipairs(alt.ways or {}) do
        if w.kind == "jump" and (not jump or (w.range or 0) > (jump.range or 0)) then jump = w end
    end
    if alt.nearest and alt.nearest.gap then
        local n = alt.nearest
        local text = string.format("the nearest you can walk to is %s m from it (%s,%s,%s)", tostring(n.gap),
            tostring(round1(n.pos[1])), tostring(round1(n.pos[2])), tostring(round1(n.pos[3])))
        if jump and jump.range then
            if n.gap <= jump.range and math.abs(n.dy or 0) < 3 then
                text = text .. string.format("; a jump (%s m) from there could cross", tostring(jump.range))
            else
                text = text .. string.format("; too far to jump (%s m)", tostring(jump.range))
            end
        end
        parts[#parts + 1] = text
    end
    local hint
    local way = nearestPassage(dy, type(target) == "string" and (uuidOf(target) or target) or nil, actor)
    if way then
        local _, line = W.Line(way)
        parts[#parts + 1] = "the nearest way you know: " .. line
        -- an open door is walked through: its `use` would close it
        hint = string.format('ai_step {"do":"%s","target":"%s"}', (way.kind == "door" and way.open) and "go" or "use", handle(uuidOf(way.id) or way.id))
    else
        parts[#parts + 1] = "no ladder, door or portal you know of leads there yet"
        hint = 'look for a way: walk on and read now.ways'
    end
    return "no path: " .. table.concat(parts, "; "), hint
end
