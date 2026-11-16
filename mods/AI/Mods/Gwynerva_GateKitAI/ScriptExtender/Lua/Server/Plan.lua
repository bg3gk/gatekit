-- AI.Plan and AI.Notes (server side): a known route followed step by step, and the agent's own
-- notes kept across loads and runs.
--
--   AI.Plan.Load(plan)     a plan by the name of a file of the mod (Plans/<name>.json) or as a table
--   AI.Plan.Line()         the next step in one line, for `now.route`; moves on by itself when the
--                          step's condition of arrival has come true in the game
--   AI.Plan.Step()         the step itself: { goal, do, target, n, spell, who, arrive, note }
--   AI.Plan.Go(to)         "next", "back", a number: the step to stand on
--   AI.Plan.Did(args)      told by AI.Step what was just done: the step it matches is passed
--   AI.Plan.Off()          no plan
--   AI.Notes.Add(text)     a note, with the place it was written at
--   AI.Notes.Near(...)     the notes written within reach of where the character stands
--
-- A plan is data: { name, about, steps = { { goal, do?, target?, n?, spell?, who?, note?, arrive? } } }.
-- `arrive` is read from the game, any of: near = { x, y, z } with within (metres, default 4);
-- y_min (the character's height at least); party (members at least); fight = "on" | "won" (a
-- fight was on and is over); waiting (a kind of thing that waits: "loot", "dialogue_choice" ...);
-- scene (a path under GKScene that is true: "finale.done"); level_not (the level's name is another).
-- A step with no `arrive` is passed when its own act was done (AI.Plan.Did) or by AI.Plan.Go.
--
-- Which plan and which step are kept in a file of the copy (Script Extender/Gwynerva_GateKitAI/
-- plan.json), and the notes beside it (notes.json): a load of a save restarts Lua, and both stay.

local GK = AI.GK
local U = AI.Util
local try = U.try

AI.Plan = AI.Plan or {}
local P = AI.Plan
local DIR = "Gwynerva_GateKitAI/"

local function save()
    local state = P.Current and { name = P.Current.name, index = P.Index, plan = P.Inline and P.Current or nil } or {}
    pcall(Ext.IO.SaveFile, DIR .. "plan.json", Ext.Json.Stringify(state))
end

local function valid(plan)
    if type(plan) ~= "table" or type(plan.steps) ~= "table" or #plan.steps == 0 then
        return "a plan is { name, steps = { { goal, do, target, arrive } ... } } with at least one step"
    end
    for i, step in ipairs(plan.steps) do
        if type(step) ~= "table" or type(step.goal) ~= "string" then return "step " .. i .. " has no goal (a text)" end
    end
end

--- Loads a plan: the name of a file of the mod (Plans/<name>.json), or the plan as a table.
--- Stands on its first step (or on `index`). { ok, name, steps } or { ok = false, error }.
function P.Load(plan, index)
    local inline = type(plan) == "table"
    if type(plan) == "string" then
        local name = plan
        if not name:match("^[%w_%-]+$") then return { ok = false, error = "a plan's name is letters, digits, _ and -" } end
        plan = AI.ReadJson("Plans/" .. name .. ".json")
        if not plan then return { ok = false, error = "no plan " .. name .. " in the mod (Plans/" .. name .. ".json)" } end
        plan.name = plan.name or name
    end
    local problem = valid(plan)
    if problem then return { ok = false, error = problem } end
    P.Current, P.Inline, P.Saw = plan, inline, {}
    P.Index = math.max(1, math.min(tonumber(index) or 1, #plan.steps + 1))
    save()
    AI.Row("ai_plan", { call = "load", name = plan.name, step = P.Index, steps = #plan.steps })
    return { ok = true, name = plan.name, steps = #plan.steps }
end

--- No plan.
function P.Off()
    P.Current, P.Index, P.Saw = nil, nil, nil
    save()
    AI.Row("ai_plan", { call = "off" })
    return { ok = true }
end

--- The step the plan stands on, or nil (no plan, or its end).
function P.Step()
    return P.Current and P.Current.steps[P.Index] or nil
end

local function sceneTrue(path)
    local at = try(function() return Mods.GateKit.GKScene end)
    for part in tostring(path):gmatch("[^%.]+") do
        if type(at) ~= "table" then return false end
        at = at[part]
    end
    return at ~= nil and at ~= false
end

local function arrived(step)
    local a = step.arrive
    if type(a) ~= "table" then return false end
    local saw = P.Saw
    local me = U.controlled()
    local p = U.posOf(me)
    if a.near then
        local point = U.pointOf(a.near)
        if not (p and point and U.dist(p, point) <= (a.within or 4)) then return false end
    end
    if a.y_min and not (p and p[2] >= a.y_min) then return false end
    if a.party and #U.members() < a.party then return false end
    if a.fight then
        local on = AI.Turn().on
        if on then saw.fight = true end
        if a.fight == "on" and not on then return false end
        if a.fight == "won" and (on or not saw.fight) then return false end
    end
    if a.waiting and not AI.Waiting().kinds[a.waiting] then return false end
    if a.scene and not sceneTrue(a.scene) then return false end
    if a.level_not then
        local level = try(function() return Ext.Entity.Get(me).Level.LevelName end)
        if level == nil or level == a.level_not then return false end
    end
    return true
end

local function advance(why)
    P.Index = P.Index + 1
    P.Saw = {}
    save()
    AI.Row("ai_plan", { call = "passed", name = P.Current.name, step = P.Index - 1, why = why })
end

--- The plan's next step in one line - "3/14 <goal> -> do=go target=..." with the step's note -
--- after passing every step whose condition of arrival is true now. nil without a plan.
function P.Line()
    local plan = P.Current
    if not plan then return nil end
    for _ = 1, #plan.steps do
        local step = P.Step()
        if not step or not arrived(step) then break end
        advance("arrived")
    end
    local step = P.Step()
    if not step then return string.format("%s: the plan is done", tostring(plan.name)) end
    local act = ""
    if step["do"] then
        act = " -> do=plan (" .. step["do"]
        for _, k in ipairs({ "target", "spell", "n", "who" }) do
            if step[k] ~= nil then act = act .. " " .. k .. "=" .. tostring(step[k]) end
        end
        act = act .. ")"
    end
    return string.format("%d/%d %s%s%s", P.Index, #plan.steps, step.goal, act, step.note and (". " .. step.note) or "")
end

--- Stands on another step: "next", "back", or its number. { ok, step, of }.
function P.Go(to)
    local plan = P.Current
    if not plan then return { ok = false, error = "no plan is loaded: do=load name=<plan>" } end
    local index = to == "next" and P.Index + 1 or to == "back" and P.Index - 1 or tonumber(to)
    if not index or index < 1 or index > #plan.steps + 1 then
        return { ok = false, error = "the plan has steps 1 to " .. #plan.steps }
    end
    P.Index, P.Saw = math.floor(index), {}
    save()
    AI.Row("ai_plan", { call = "go", name = plan.name, step = P.Index })
    return { ok = true, step = P.Index, of = #plan.steps }
end

--- Told what AI.Step has just done: a step of the plan with no condition of arrival, whose act
--- (`do`, and `target` when it names one) is this, is passed.
function P.Did(args)
    local step = P.Step()
    if not step or step.arrive or not step["do"] then return false end
    if step["do"] ~= args["do"] then return false end
    if step.target ~= nil and args.target ~= nil then
        local a, b = AI.Resolve(step.target), AI.Resolve(args.target)
        local same = type(a) == "string" and a == b
            or (type(a) == "table" and type(b) == "table" and U.dist(a, b) < 1)
        if not same then return false end
    end
    advance("done")
    return true
end

if P.Current == nil and not P.Restored then
    P.Restored = true
    local ok, text = pcall(Ext.IO.LoadFile, DIR .. "plan.json")
    local state = ok and text and text ~= "" and select(2, pcall(Ext.Json.Parse, text)) or nil
    if type(state) == "table" and (state.plan or state.name) then
        local plan = state.plan or AI.ReadJson("Plans/" .. tostring(state.name) .. ".json")
        if plan and not valid(plan) then
            plan.name = plan.name or state.name
            P.Current, P.Inline, P.Saw = plan, state.plan ~= nil, {}
            P.Index = math.max(1, math.min(tonumber(state.index) or 1, #plan.steps + 1))
        end
    end
end

-- ---------------------------------------------------------------------------------------
-- Notes

AI.Notes = AI.Notes or {}
local N = AI.Notes
N.Reach = N.Reach or 15      -- metres: a note is told again within this of where it was written
N.Max = N.Max or 200

local function loadNotes()
    if N.List then return N.List end
    local ok, text = pcall(Ext.IO.LoadFile, DIR .. "notes.json")
    local list = ok and text and text ~= "" and select(2, pcall(Ext.Json.Parse, text)) or nil
    N.List = type(list) == "table" and list or {}
    return N.List
end

--- Keeps a note of the agent's own - what it learnt of a place or a fight, a mistake not to
--- repeat - with the level and the point the controlled character stands at. The notes are a
--- file of the game copy: they outlive a load, a reset and the game's restart. { ok, n }.
function N.Add(text)
    text = U.cut(text, 200)
    if text == "" then return { ok = false, error = "an empty note" } end
    local list = loadNotes()
    local me = U.controlled()
    local p = U.posOf(me)
    local level = try(function() return Ext.Entity.Get(me).Level.LevelName end)
    list[#list + 1] = { text = text, level = level, at = p and { U.round1(p[1]), U.round1(p[2]), U.round1(p[3]) } or nil }
    while #list > N.Max do table.remove(list, 1) end
    pcall(Ext.IO.SaveFile, DIR .. "notes.json", Ext.Json.Stringify(list))
    AI.Row("ai_note", { text = text, level = level, at = list[#list].at })
    -- whoever has just written it is not told it back while it stays here
    for _, session in pairs(AI.Sessions or {}) do
        session.notes = session.notes or {}
        session.notes[#list] = true
    end
    return { ok = true, n = #list }
end

--- The notes written on this level within AI.Notes.Reach of the controlled character (every
--- note of the level with `all`): { { n, text } }, the oldest first.
function N.Near(all)
    local out = {}
    local me = U.controlled()
    local p = U.posOf(me)
    local level = try(function() return Ext.Entity.Get(me).Level.LevelName end)
    for n, note in ipairs(loadNotes()) do
        if note.level == level and (all or (p and note.at and U.dist(p, note.at) <= N.Reach)) then
            out[#out + 1] = { n = n, text = note.text }
        end
    end
    return out
end

--- Forgets every note. { ok, forgot }.
function N.Clear()
    local n = #loadNotes()
    N.List = {}
    pcall(Ext.IO.SaveFile, DIR .. "notes.json", "[]")
    return { ok = true, forgot = n }
end
