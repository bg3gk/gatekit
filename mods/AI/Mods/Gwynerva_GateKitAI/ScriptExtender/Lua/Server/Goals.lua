-- AI.Goals (server side): the agent's own plan - up to three short goals it writes with a step
-- (`goals`), keeps across steps, marks done, and is told back in `now.goals` when they change and
-- when a party member's turn opens. Whoever watches sees them over the acting character's head, one
-- after another, open goals in the line's violet, done ones dimmed with a tick. Nothing in them is
-- the bridge's: the agent writes them, the bridge keeps and shows them.
--
--   AI.SetGoals(text, who)   "goal | ✓ done goal | goal" (or one per line); "" forgets them
--   AI.GoalLines()           { "1. goal", "2. ✓ done goal" }
--   AI.ShowGoals(who)        the goals over `who`'s head, AI.GoalsShow.rounds times in a row

AI.Goals = AI.Goals or { items = {} }
AI.GoalsShow = AI.GoalsShow or { max = 3, chars = 60, gap = 2.0, rounds = 2,
                                 open = { color = "#B340FF", bold = true }, done = { color = "#B0B0B0", bold = true } }
local GK = AI.GK
local U = AI.Util
local S = AI.GoalsShow

local DONE = { "✓", "+", "done:", "[x]" }

local function parse(text)
    local items = {}
    for part in (tostring(text) .. "|"):gmatch("([^|\n]*)[|\n]") do
        local t = part:gsub("^%s+", ""):gsub("%s+$", "")
        if t ~= "" and #items < S.max then
            local done = false
            for _, mark in ipairs(DONE) do
                if t:sub(1, #mark):lower() == mark then done, t = true, t:sub(#mark + 1):gsub("^%s+", "") end
            end
            items[#items + 1] = { text = U.cut(t, S.chars), done = done }
        end
    end
    return items
end

function AI.GoalLines()
    local out = {}
    for i, g in ipairs(AI.Goals.items or {}) do out[i] = string.format("%d. %s%s", i, g.done and "✓ " or "", g.text) end
    return out
end

-- The lines waiting to be shown: { who, text, style }, one every S.gap seconds of the game's time.
local queue, waitLeft, subscription = {}, 0, nil
local function pump(e)
    local dt = 1 / 30
    pcall(function() dt = e.Time.DeltaTime end)
    waitLeft = waitLeft - dt
    if waitLeft > 0 then return end
    local nextLine = table.remove(queue, 1)
    if not nextLine then
        Ext.Events.Tick:Unsubscribe(subscription)
        subscription = nil
        return
    end
    pcall(function() GK.Unobserved(GK.Say.Line(nextLine.who, nextLine.text, { style = nextLine.style })) end)
    waitLeft = S.gap
end

function AI.ShowGoals(who)
    who = who or U.controlled()
    local items = AI.Goals.items or {}
    if not who or #items == 0 then return false end
    queue = {}
    for _ = 1, S.rounds do
        for i, g in ipairs(items) do
            queue[#queue + 1] = { who = who, text = string.format("%d. %s%s", i, g.done and "✓ " or "", g.text), style = g.done and S.done or S.open }
        end
    end
    waitLeft = 0
    if not subscription then subscription = Ext.Events.Tick:Subscribe(pump) end
    return true
end

function AI.SetGoals(text, who)
    local items = text == "" and {} or parse(text)
    AI.Goals = { items = items }
    AI.Row("ai_goals", { goals = AI.GoalLines() })
    if #items > 0 then AI.ShowGoals(who) end
    return items
end
