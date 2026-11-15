-- Loaded into every test before its own code: the small helpers a test needs to say
-- what Python said with `assert x == y, (a, b)`. Everything here is plain Lua; the `game`
-- object and the file helpers come from the host (see README.md).

--- Renders any value as a one-line string, for an assertion message.
function fmt(value, seen)
    local kind = type(value)
    if kind == "string" then return string.format("%q", value) end
    if kind ~= "table" then return tostring(value) end
    seen = seen or {}
    if seen[value] then return "<cycle>" end
    seen[value] = true
    local parts, n = {}, 0
    for i, item in ipairs(value) do
        n = i
        parts[#parts + 1] = fmt(item, seen)
    end
    local named = {}
    for key in pairs(value) do
        if not (type(key) == "number" and key >= 1 and key <= n and key % 1 == 0) then named[#named + 1] = tostring(key) end
    end
    table.sort(named)
    for _, key in ipairs(named) do parts[#parts + 1] = key .. " = " .. fmt(value[key], seen) end
    seen[value] = nil
    return "{" .. table.concat(parts, ", ") .. "}"
end

--- Deep equality (lists and records alike); numbers may differ by `tolerance`.
function same(a, b, tolerance)
    if type(a) == "number" and type(b) == "number" and tolerance then return math.abs(a - b) <= tolerance end
    if a == b then return true end
    if type(a) ~= "table" or type(b) ~= "table" then return false end
    for key, value in pairs(a) do
        if not same(value, b[key], tolerance) then return false end
    end
    for key in pairs(b) do
        if a[key] == nil then return false end
    end
    return true
end

--- Two lists of numbers that agree to `tolerance` (the Python tests' `near`).
function near(a, b, tolerance)
    tolerance = tolerance or 0.02
    if type(a) ~= "table" or type(b) ~= "table" then return false end
    for i, value in ipairs(b) do
        if type(a[i]) ~= "number" or math.abs(a[i] - value) > tolerance then return false end
    end
    return true
end

--- The first row of `kind` (a name or a list) after the cursor `since` (a game.mark()) that
--- `match(payload, row)` accepts: game.wait_row.
function row(since, kind, match, seconds) return game.wait_row(since, kind, match, seconds) end

--- assert() that renders whatever it is given: check(cond, "what went wrong", value, ...)
function check(condition, message, ...)
    if condition then return condition end
    local parts = { tostring(message or "check failed") }
    for i = 1, select("#", ...) do parts[#parts + 1] = fmt((select(i, ...))) end
    error(table.concat(parts, ": "), 2)
end

--- check(same(got, wanted), ...), with both sides in the message.
function eq(got, wanted, message)
    if same(got, wanted) then return got end
    error((message or "not equal") .. ": got " .. fmt(got) .. ", wanted " .. fmt(wanted), 2)
end

--- A sorted copy of a list.
function sorted(list, compare)
    local out = {}
    for i, value in ipairs(list) do out[i] = value end
    table.sort(out, compare)
    return out
end

--- The keys of a record, sorted by their text.
function keys(record)
    local out = {}
    for key in pairs(record or {}) do out[#out + 1] = key end
    table.sort(out, function(a, b) return tostring(a) < tostring(b) end)
    return out
end

--- Every value of a list or record, sorted by their text.
function values(record)
    local out = {}
    for _, value in pairs(record or {}) do out[#out + 1] = value end
    table.sort(out, function(a, b) return tostring(a) < tostring(b) end)
    return out
end

--- Whether a list holds a value.
function has(list, wanted)
    for _, value in ipairs(list or {}) do
        if same(value, wanted) then return true end
    end
    return false
end

--- How many entries a record has (lists: #list).
function count(record)
    local n = 0
    for _ in pairs(record or {}) do n = n + 1 end
    return n
end

--- Plain substring search (no patterns).
function contains(text, part)
    return type(text) == "string" and string.find(text, part, 1, true) ~= nil
end

function startswith(text, part) return string.sub(text or "", 1, #part) == part end
function endswith(text, part) return part == "" or string.sub(text or "", -#part) == part end

--- "a, b, c" from a list (empty: `empty`).
function join(list, separator, empty)
    local parts = {}
    for i, value in ipairs(list or {}) do parts[i] = tostring(value) end
    if #parts == 0 then return empty or "" end
    return table.concat(parts, separator or ", ")
end

--- A Lua list literal of strings: lua_list{"a","b"} -> {"a", "b"}
function lua_list(list)
    local parts = {}
    for i, value in ipairs(list) do parts[i] = string.format("%q", tostring(value)) end
    return "{" .. table.concat(parts, ", ") .. "}"
end

-- bytes(text): how many bytes the text takes as UTF-8 (what the game counts; the host's `#`
-- counts characters, so the two differ wherever the text is not plain ASCII). It comes from
-- the host, because this interpreter's string.byte does not know characters above ASCII.

--- A long-bracket Lua string literal, for text handed to the game as code.
function lua_string(text) return "[==[" .. text .. "]==]" end

--- What the Lua expression `expression` reads, once three readings in a row agree: nothing
--- moves by itself any more (the overlay test's `settled`).
function settled(expression, side)
    local last, agreed = nil, 0
    for _ = 1, 40 do
        local now = game.lua(side or "client", "return " .. expression)
        agreed = same(now, last) and agreed + 1 or 0
        if agreed >= 3 then return now end
        last = now
        game.sleep(0.15)
    end
    error("the layout never came to rest: " .. fmt(last), 2)
end

--- Starts counting, every tick on `side` ("client" or "server"), how many entities hold the
--- component `name` (the engine's own name, e.g. "esv::lock::LockComponent"). Started before the
--- steps of a recipe, it sees a component that lives for one frame, which a reading afterwards
--- would miss. The count lives in the game's Lua state (GKTestWatches) until `alive` reads it.
--- A name that is not a component type - a bare namespace such as "esv", a system - is refused here:
--- the engine knows no holders of it, and the replay would fail as if the component never came.
local function component_name(name)
    check(type(name) == "string" and name:find("::", 1, true) ~= nil and not endswith(name, "::")
        and not endswith(name, "System"),
        "not a component type: " .. tostring(name) .. " (the engine's full name is wanted, e.g. esv::lock::LockComponent)")
end

function watch(side, name)
    component_name(name)
    local code = string.format([[
        GKTestWatches = GKTestWatches or {}
        local name = %q
        local old = GKTestWatches[name]
        if old and old.Subscription then Ext.Events.Tick:Unsubscribe(old.Subscription) end
        local w = { Max = 0, Ticks = 0, Before = 0, Held = {}, New = false }
        local ok, held = pcall(GK.Probe.Holders, name, 64)
        if not ok then return { Error = tostring(held) } end
        w.Before = #held
        for _, entity in ipairs(held) do w.Held[tostring(entity)] = true end
        GKTestWatches[name] = w
        w.Subscription = Ext.Events.Tick:Subscribe(function()
            w.Ticks = w.Ticks + 1
            local fine, now = pcall(GK.Probe.Holders, name, 64)
            if not fine then w.Error = tostring(now) return end
            if #now > w.Max then w.Max = #now end
            if #now > 0 and w.First == nil then w.First = w.Ticks end
            if not w.New then
                for _, entity in ipairs(now) do
                    if not w.Held[tostring(entity)] then w.New = true break end
                end
            end
        end)
        return { Before = w.Before }
    ]], name)
    local answer = game.lua(side, code)
    check(type(answer) == "table" and answer.Error == nil, "cannot watch " .. name .. " on " .. side, answer)
    return answer.Before
end

--- Fails the test unless `name` was held by some entity on `side` since `watch(side, name)`,
--- or is held now; waits up to `seconds` (default 10) for it. Stops the watch. Returns what it saw:
--- { before = holders when the watch began, most = the most holders in one tick, now = holders
--- at the end, ticks = ticks watched, first = the tick it was first seen, new = an entity held it
--- that did not when the watch began }.
--- A type already held when the watch began proves nothing by being held after the steps, so that
--- FAILS - unless `options` says `{ may_exist = true }` (a persistent type), and then the holder
--- count must rise above what it was, or a new entity must hold it.
--- What it saw is kept in RECIPE_ALIVE for the runner, which writes it into replays.jsonl.
function alive(side, name, seconds, options)
    component_name(name)
    local may = type(options) == "table" and options.may_exist == true
    local code = string.format([[
        local w = (GKTestWatches or {})[%q]
        local ok, held = pcall(GK.Probe.Holders, %q, 64)
        return { Watched = w ~= nil, Most = w and w.Max or 0, Ticks = w and w.Ticks or 0, First = w and w.First,
                 Before = w and w.Before or 0, Error = w and w.Error or (not ok and tostring(held) or nil),
                 Now = ok and #held or 0, New = w and w.New or false }
    ]], name, name)
    local function came(seen)
        if may then return seen.Most > seen.Before or seen.Now > seen.Before or seen.New == true end
        return seen.Most > 0 or seen.Now > 0
    end
    local deadline = game.now() + (seconds or 10)
    local seen
    repeat
        seen = game.lua(side, code)
        if seen.Error or came(seen) or (not may and seen.Before > 0) then break end
        game.sleep(0.25)
    until game.now() > deadline
    game.lua(side, string.format([[
        local w = (GKTestWatches or {})[%q]
        if w and w.Subscription then Ext.Events.Tick:Unsubscribe(w.Subscription) end
        if GKTestWatches then GKTestWatches[%q] = nil end
    ]], name, name))
    RECIPE_ALIVE = { side = side, component = name, before = seen.Before, most = seen.Most, now = seen.Now,
                     new = seen.New == true, may_exist = may }
    check(seen.Error == nil, name .. " could not be read on " .. side, seen.Error)
    check(may or seen.Before == 0, name .. " was already held by " .. tostring(seen.Before) .. " entities on " .. side
        .. " before the steps, so its being held after them proves nothing. A recipe of a type that persists passes"
        .. " alive(side, name, seconds, { may_exist = true }) and must then make the holder count rise or a new holder appear", seen)
    check(came(seen), name .. (may and (" was held by " .. tostring(seen.Before) .. " entities on " .. side
            .. " before the steps and the steps added none: no more holders and no new one")
        or (" never came alive on " .. side))
        .. (seen.Watched and (" in " .. seen.Ticks .. " ticks watched") or " (it was not watched: call watch first)"), seen)
    return { before = seen.Before, most = seen.Most, now = seen.Now, ticks = seen.Ticks, first = seen.First, new = seen.New == true }
end

-- expect: claims the game can check (research/coverage/systems.md) ---------------------------------
--
-- Four verbs and no more, each given a table in the shape a claim writes in JSON, each returning a
-- verdict { verb, ok, detail, entity? } and never throwing:
--   expect.appears{ component = C, side = "server", as = "E"?, within = 1 }
--   expect.gone{ component = C, side = "server", entity = uuid? | on = "E"?, within = 1 }
--   expect.equals{ component = C, field = F, value = V, side = "server", entity = uuid? | on = "E"?, within = 1 }
--   expect.changes{ component = C, field = F, side = "server", entity = uuid? | on = "E"?, within = 1 }
-- A field is read by the record's name (GK.Probe.Field: the layouts the host hands the bridge).
--
-- Between expect.begin(list) and expect.finish(list) every expectation is watched EVERY TICK in
-- the game, so what lives one frame is seen, and what happens in between counts:
--   appears  - some entity held C at some tick; with `as`, the first entity that did not hold it at
--              begin is bound to that name;
--   gone     - an entity that held C (at begin, or later) stopped holding it - so "added, then
--              removed inside the recipe" passes;
--   equals   - at some tick some holder's F was V (one-frame types included);
--   changes  - the same entity's F held one value and then another while it held C (a new holder
--              alone does not pass);
--   on = "E" - judged on the entity `as = "E"` bound (that expectation comes earlier in the list).
-- `within` is how long finish waits for it after the steps (default 1 s). A verb called on its own
-- watches from that moment for `within` seconds.

expect = {}

local function verdict(verb, ok, detail, entity) return { verb = verb, ok = ok == true, detail = detail, entity = entity } end

local function ask(side, code)
    local ok, answer = pcall(game.lua, side, code)
    if not ok then return nil, tostring(answer) end
    return answer
end

local function same(x, y)
    if type(x) == "number" and type(y) == "number" then return math.abs(x - y) <= 1e-4 * math.max(1, math.abs(x)) end
    return x == y
end

local VERBS = { "appears", "gone", "equals", "changes" }
local function spec(item)
    for _, verb in ipairs(VERBS) do
        if item[verb] then return verb, item[verb] end
    end
    return nil, nil
end

-- The watch, in the game: every tick, the holders of C and (with a field) each one's value.
-- GKExpect[key] = { Ticks, Entities = { [id] = { AtBegin, First, Gone, Values = { {tick, value} } } } }
local WATCH = [[
GKExpect = GKExpect or {}
local key, component, field, only = %q, %q, %s, %s
local old = GKExpect[key]
if old and old.Subscription then Ext.Events.Tick:Unsubscribe(old.Subscription) end
local w = { Ticks = 0, Entities = {}, Errors = {} }
GKExpect[key] = w
local function look(atBegin)
    local held = {}
    local list = only and { Ext.Entity.Get(only) } or GK.Probe.Holders(component, 64)
    for _, entity in ipairs(list) do
        if entity then
            local id = tostring(Ext.Utils.HandleToInteger(entity))
            local holds = true
            if only then
                holds = false
                for _, h in ipairs(GK.Probe.Holders(component, 256)) do
                    if Ext.Utils.HandleToInteger(h) == Ext.Utils.HandleToInteger(entity) then holds = true end
                end
            end
            if holds then
                held[id] = true
                local e = w.Entities[id]
                if not e then e = { AtBegin = atBegin, First = w.Ticks, Values = {} }; w.Entities[id] = e end
                e.Gone = nil
                if field then
                    local value, why = GK.Probe.Field(entity, component, field)
                    if value == nil then
                        if #w.Errors < 3 then w.Errors[#w.Errors + 1] = tostring(why) end
                    else
                        local last = e.Values[#e.Values]
                        if (not last or last[2] ~= value) and #e.Values < 32 then e.Values[#e.Values + 1] = { w.Ticks, value } end
                    end
                end
            end
        end
    end
    for id, e in pairs(w.Entities) do
        if not held[id] and not e.Gone then e.Gone = w.Ticks end
    end
end
local ok, problem = pcall(look, true)
if not ok then return { Error = tostring(problem) } end
w.Subscription = Ext.Events.Tick:Subscribe(function()
    w.Ticks = w.Ticks + 1
    local fine, why = pcall(look, false)
    if not fine and #w.Errors < 3 then w.Errors[#w.Errors + 1] = tostring(why) end
end)
return { Ticks = 0 }
]]

local READ = [[
local w = (GKExpect or {})[%q]
if not w then return nil end
if %s and w.Subscription then Ext.Events.Tick:Unsubscribe(w.Subscription); w.Subscription = nil end
return { Ticks = w.Ticks, Entities = w.Entities, Errors = w.Errors }
]]

local state = { keys = {}, bound = {} }

local function valuesOf(e)
    local out = {}
    for _, v in ipairs(e.Values or {}) do out[#out + 1] = v[2] end
    return out
end

local function judge(verb, a, w, bound)
    if not w then return verdict(verb, false, "not watched (the watch could not start)") end
    local entities = w.Entities or {}
    local only
    if a.on then
        only = bound[a.on]
        if not only then return verdict(verb, false, "`on = " .. tostring(a.on) .. "`: no earlier `as` bound it") end
    end
    local function each(fn)
        for id, e in pairs(entities) do
            if not only or id == only then
                local r = fn(id, e)
                if r then return r end
            end
        end
    end
    local errors = (type(w.Errors) == "table" and #w.Errors > 0) and (" (" .. table.concat(w.Errors, "; ") .. ")") or ""
    if verb == "appears" then
        local found = each(function(id, e) if not e.AtBegin then return id end end)
        local any = found or each(function(id) return id end)
        if not any then return verdict(verb, false, "never held in " .. tostring(w.Ticks) .. " ticks watched" .. errors) end
        if a.as then
            if not found then return verdict(verb, false, "held, but by no entity that lacked it at the start: nothing to bind to " .. tostring(a.as)) end
            bound[a.as] = found
        end
        return verdict(verb, true, (found and ("entity " .. found .. " came to hold it") or ("held by entity " .. any .. " from the start"))
            .. " in " .. tostring(w.Ticks) .. " ticks", found or any)
    elseif verb == "gone" then
        local id = each(function(id, e) if e.Gone then return id end end)
        if id then return verdict(verb, true, "entity " .. id .. " stopped holding it at tick " .. tostring(entities[id].Gone), id) end
        return verdict(verb, false, "no holder stopped holding it in " .. tostring(w.Ticks) .. " ticks" .. errors)
    elseif verb == "equals" then
        local id = each(function(id, e)
            for _, v in ipairs(valuesOf(e)) do if same(v, a.value) then return id end end
        end)
        if id then return verdict(verb, true, tostring(a.field) .. " = " .. fmt(a.value) .. " on entity " .. id, id) end
        local seen = {}
        each(function(id, e) seen[id] = valuesOf(e) end)
        return verdict(verb, false, tostring(a.field) .. " read " .. fmt(seen) .. errors)
    elseif verb == "changes" then
        local id = each(function(id, e) if #(e.Values or {}) >= 2 then return id end end)
        if id then return verdict(verb, true, tostring(a.field) .. " of entity " .. id .. " went " .. fmt(valuesOf(entities[id])), id) end
        local seen = {}
        each(function(id, e) seen[id] = valuesOf(e) end)
        return verdict(verb, false, "no holder's " .. tostring(a.field) .. " changed while it held it: " .. fmt(seen) .. errors)
    end
    return verdict(verb, false, "an expectation is one of appears, gone, equals, changes")
end

--- Before the steps: one tick-by-tick watch per expectation.
function expect.begin(list)
    state = { keys = {}, bound = {} }
    for i, item in ipairs(list or {}) do
        local verb, a = spec(item)
        if verb and type(a) == "table" and a.component then
            local key = "x" .. i .. ":" .. verb .. ":" .. tostring(a.component) .. ":" .. tostring(a.field)
            local field = (verb == "equals" or verb == "changes") and a.field and string.format("%q", a.field) or "nil"
            local entity = a.entity and string.format("%q", a.entity) or "nil"
            local answer = ask(a.side or "server", string.format(WATCH, key, a.component, field, entity))
            if type(answer) == "table" and answer.Error == nil then state.keys[i] = key end
        end
    end
end

--- After the steps: one verdict per expectation, in order (an `as` binds for the ones after it).
function expect.finish(list)
    local out = {}
    for i, item in ipairs(list or {}) do
        local verb, a = spec(item)
        if not verb or type(a) ~= "table" then
            out[i] = verdict("?", false, "an expectation is one of appears, gone, equals, changes")
        elseif not a.component then
            out[i] = verdict(verb, false, "needs component")
        elseif (verb == "equals" or verb == "changes") and not a.field then
            out[i] = verdict(verb, false, "needs field")
        else
            -- the steps may have just caused it: give the watch up to `within` seconds to see it
            local deadline = game.now() + (a.within or 1)
            local w
            repeat
                w = state.keys[i] and ask(a.side or "server", string.format(READ, state.keys[i], "false")) or nil
                local ok, r = pcall(judge, verb, a, w, {})
                if ok and r.ok then break end
                if game.now() >= deadline then break end
                game.sleep(0.25)
            until false
            w = state.keys[i] and ask(a.side or "server", string.format(READ, state.keys[i], "true")) or nil
            local ok, result = pcall(judge, verb, a, w, state.bound)
            out[i] = ok and result or verdict(verb, false, tostring(result))
        end
    end
    return out
end

-- A verb on its own: watches from now for `within` seconds.
local function alone(verb)
    return function(a)
        if type(a) ~= "table" or not a.component then return verdict(verb, false, "needs component") end
        local list = { { [verb] = a } }
        local saved = state
        expect.begin(list)
        local result = expect.finish(list)[1]
        state = saved
        return result
    end
end
for _, verb in ipairs(VERBS) do expect[verb] = alone(verb) end
