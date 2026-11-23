-- AI.SessionCall, AI.Step, AI.Look, AI.Scene, AI.Tool (server side): the four tools of the bridge.
--
--   AI.SessionCall(args) the session begun (mode, the owner's prompt, a plan) or read
--   AI.Step(args)       one act, then the wait until the game has settled, then the answer
--   AI.Look(args)       the whole picture, or one part of it
--   AI.Scene(args)      a scene under GKScene: its state, or one of its calls run
--   AI.Tool(name, args) any of the four as the text a tool answers: one line of JSON, the keys
--                       ok, mode, did, why_not, hint, now always there and in that order
--   AI.Acts             the acts of a step, by name: data
--   AI.Cap, AI.Quiet    the limits of the wait, in the server's ticks
--
-- An act goes the player's own way where GateKit has it (GK.Act, GK.Gui: `fair`), by GateKit's
-- scripted order where the player's click is not built (`ordered`: the character still walks,
-- reaches and uses as the game makes it), and two are cheats by name.

local GK = AI.GK
local U = AI.Util
local try, uuidOf, nameOf, handle, cut = U.try, U.uuidOf, U.nameOf, U.handle, U.cut

AI.Cap = AI.Cap or 1200      -- ticks a step waits for the game at most (forty seconds of a running game)
AI.Quiet = AI.Quiet or 8     -- ticks with nothing going on after which the game counts as settled
local MIN = 3                -- ticks after the act before the first look

local ORDER = { "ok", "mode", "did", "why_not", "hint", "prompt", "now" }

local function answer(ok, did, why, hint)
    return { ok = ok, mode = AI.Mode.name, did = did or "", why_not = why or "", hint = hint or "" }
end

local function call(tool, args)
    local parts = {}
    for _, k in ipairs({ "do", "target", "spell", "n", "who", "window", "control", "value", "part", "name", "call", "file", "think" }) do
        if args[k] ~= nil then
            parts[#parts + 1] = string.format(type(args[k]) == "number" and '"%s":%s' or '"%s":"%s"', k, tostring(args[k]))
        end
    end
    return tool .. " {" .. table.concat(parts, ",") .. "}"
end

local function label(target)
    if type(target) == "table" then return string.format("%s,%s,%s", U.round1(target[1]), U.round1(target[2]), U.round1(target[3])) end
    return string.format("%s %s", tostring(nameOf(target)), handle(target))
end

-- ---------------------------------------------------------------------------------------
-- The acts

-- Follows what a GateKit verb answered to its end: an observed answer, then the job it started.
-- The poll asked `"begun"` says whether the verb has taken the act on (not refused it).
local function follow(result)
    local step = GK.Follow(result)
    local final
    return function(ask)
        if ask == "begun" then return final ~= nil and not (type(final) == "table" and final.ok == false) end
        if final == nil then
            final = step()
            if final == nil then return nil end
        end
        if type(final) == "table" and final.job then
            local job = GK.Act.Job(final.job)
            if type(job) == "table" and job.status == "running" then return nil end
            if type(job) == "table" and job.status == "failed" then
                return { ok = false, error = tostring(job.error or "the game did not carry it out") }
            end
            return { ok = true, result = type(job) == "table" and job.result or nil }
        end
        if type(final) == "table" and final.ok == false then
            return { ok = false, error = tostring(final.error or final.saw or final.did or "refused"), detail = final }
        end
        return { ok = true, result = final }
    end
end

-- An act of the client: asked over the mod's channel, answered when the client has seen its end.
local function client(act)
    local got
    AI.Ask(act, function(a) got = a end)
    return function(ask)
        if ask == "begun" then return false end
        if got == nil then return nil end
        if type(got) == "table" and got.ok == false then
            return { ok = false, error = tostring(got.error or got.saw or got.did or "refused"), detail = got }
        end
        return { ok = true, result = got }
    end
end

local CLOSE = { tutorial = "close", loot = "close", trade = "close", pickpocket = "close", learn = "close", book = "close",
                split = "cancel", save = "close", load = "close", gamemenu = "resume", rest = "cancel", box = "negative" }

-- way: fair (the player's own path), ordered (GateKit's scripted order: the click is not built),
-- cheat. needs: the arguments that must be given. answers: it answers what waits (allowed while
-- something waits). turn: in a fight it needs a party member's open turn.
AI.Acts = AI.Acts or {}
local A = AI.Acts
A.go = { way = "ordered", needs = { "target" }, turn = true, about = "walk to a thing or a point",
    run = function(c) return follow(GK.Cheat.Move(c.actor, c.target, { partial = true, provoke = c.provoke })), "walked to " .. label(c.target) end }
A.use = { way = "ordered", needs = { "target" }, entity = true, turn = true, about = "walk up and use: a door, a lever, a ladder, a pod",
    run = function(c)
        if type(c.target) == "string" then AI.Tried[uuidOf(c.target) or c.target] = true end
        return follow(GK.Cheat.Use(c.actor, c.target)), "used " .. label(c.target)
    end }
A.talk = { way = "ordered", needs = { "target" }, entity = true, about = "walk up and talk",
    run = function(c) return follow(GK.Cheat.Talk(c.actor, c.target)), "asked " .. label(c.target) .. " to talk" end }
A.loot = { way = "ordered", needs = { "target" }, entity = true, about = "walk up and open a body or a container",
    run = function(c) return follow(GK.Cheat.Loot(c.actor, c.target)), "opened " .. label(c.target) end }
A.jump = { way = "ordered", needs = { "target" }, turn = true, about = "jump to a point",
    run = function(c) return follow(GK.Cheat.Jump(c.actor, type(c.target) == "table" and c.target or U.posOf(c.target))), "jumped to " .. label(c.target) end }
A.cast = { way = "fair", needs = { "spell", "target" }, turn = true, about = "cast a spell or use an action at a thing or a point",
    run = function(c) return client({ act = "cast", spell = c.spell, target = c.target }), "cast " .. c.spell .. " at " .. label(c.target) end }
A.attack = { way = "fair", needs = { "target" }, entity = true, turn = true, about = "the weapon attack",
    run = function(c) return client({ act = "attack", target = c.target }), "attacked " .. label(c.target) end }
A["end"] = { way = "fair", turn = true, about = "end the turn",
    run = function(c) return follow(GK.Act.EndTurn(c.actor)), "ended the turn of " .. tostring(nameOf(c.actor)) end }
A.select = { way = "fair", needs = { "who" }, about = "take control of a party member",
    run = function(c)
        local did = "took control of " .. tostring(nameOf(c.actor))
        -- in a fight the acts go to the member whose turn is open, whoever is controlled
        local t = AI.Turn()
        if t.on and t.mine and (uuidOf(t.mine) or t.mine) ~= (uuidOf(c.actor) or c.actor) then
            did = did .. "; the open turn is " .. tostring(nameOf(t.mine)) .. "'s: acts without who are still " .. tostring(nameOf(t.mine)) .. "'s"
        end
        return follow(GK.Act.Select(c.actor)), did
    end }
AI.Examined = AI.Examined or {}
A.examine = { way = "fair", needs = { "target" }, entity = true, about = "examine a creature: the game's Examine window, as the T key opens it",
    run = function(c) return client({ act = "examine", target = uuidOf(c.target) or c.target }), "examined " .. label(c.target) end }
A.answer = { way = "fair", needs = { "n" }, answers = true, about = "choose answer n of what waits",
    run = function(c)
        local n = c.n
        return client({ act = "resolve", only = { dialogue = true, reaction = true, box = true, reward = true, rest = true },
            policy = { dialogue = n, reaction = n == 0 and "decline" or n, box = n, reward = n, rest = n == 1 and "rest" or "cancel" } }),
            "chose answer " .. tostring(n)
    end }
A.continue = { way = "fair", answers = true, about = "go on in a dialogue",
    guard = function()
        local choice = AI.Waiting().choice
        if choice and choice > 1 then
            return "a choice of " .. choice .. " answers waits: going on would choose for you", call("ai_step", { ["do"] = "answer", n = 1 })
        end
    end,
    run = function() return client({ act = "resolve", only = { dialogue = true }, policy = { dialogue = 1 } }), "went on" end }
A.roll = { way = "fair", answers = true, about = "throw the dice of a roll window",
    run = function() return client({ act = "resolve", only = { roll = true }, policy = { roll = "throw" }, again = "roll" }), "threw the dice" end }
A.take = { way = "fair", answers = true, about = "take everything from the open loot window",
    run = function() return client({ act = "resolve", only = { loot = true }, policy = { loot = "takeall" } }), "took everything" end }
A.close = { way = "fair", answers = true, about = "close the window that waits; skip a film",
    run = function()
        -- a film is skipped by the game's own key, as a player skips it
        if AI.Client.stuck == "film" then return client({ act = "key", key = "ESCAPE" }), "skipped the film" end
        return client({ act = "resolve", policy = CLOSE, never = { dialogue = true, roll = true } }), "closed the window"
    end }
A.ui = { way = "fair", needs = { "window" }, answers = true, about = "a window's controls: read them, click one, set one",
    run = function(c)
        local what = (c.control == nil and "looked at " or (c.value ~= nil and "set " or "clicked ")) .. c.window
            .. (c.control and ("/" .. c.control) or "") .. (c.value and (c.control and (" = " .. c.value) or (" for " .. c.value)) or "")
        return client({ act = "ui", window = c.window, control = c.control, value = c.value }), what
    end }
A.wait = { way = "fair", answers = true, about = "do nothing until the game has settled",
    run = function() return nil, "waited" end }
A.cheat_cast = { way = "cheat", needs = { "spell", "target" }, turn = true, about = "CHEAT: cast by script a spell the hotbar does not hold",
    run = function(c) return follow(GK.Cheat.Cast(c.actor, c.spell, c.target, { walk = true })), "cast " .. c.spell .. " at " .. label(c.target) .. " by script" end }
A.cheat_teleport = { way = "cheat", needs = { "target" }, about = "CHEAT: put the character at a thing or a point",
    run = function(c)
        local p = type(c.target) == "table" and c.target or U.posOf(c.target)
        if not p then return function() return { ok = false, error = "that thing has no place" } end, "" end
        GK.Teleport(p[1], p[2], p[3], c.actor)
        return nil, "teleported to " .. label(c.target)
    end }

local function actNames()
    local names = {}
    for name in pairs(A) do names[#names + 1] = name end
    names[#names + 1] = "plan"
    table.sort(names)
    return table.concat(names, ", ")
end

-- The stats name of what `text` names among the actor's actions: the id itself, or the one action
-- whose id or name holds the text. nil and the ready ones otherwise.
local function spellOf(actor, text)
    local wanted = tostring(text):lower()
    local actions = try(GK.See.Actions, actor) or {}
    local part, ready = nil, {}
    for _, a in ipairs(actions) do
        local id = tostring(a.id)
        if id:lower() == wanted or (a.name and a.name:lower() == wanted) then return id end
        if id:lower():find(wanted, 1, true) or (a.name and a.name:lower():find(wanted, 1, true)) then part = part or id end
        if a.ready and #ready < 8 then ready[#ready + 1] = id end
    end
    if part then return part end
    if #actions == 0 and try(Ext.Stats.Get, text) then return text end
    return nil, ready
end

-- ---------------------------------------------------------------------------------------
-- Settled

-- Why the game is still going on by itself, or nil.
local function busy(state)
    if next(AI.Asked) ~= nil then return "the act is still being carried out" end
    local waits = AI.Waiting()
    if waits.busy then return waits.busy end
    local turn = AI.Turn()
    if turn.on and not turn.mine then return "others take their turns" end
    local p = U.posOf(U.controlled())
    local moved = p and state.p and U.dist(p, state.p) > 0.05
    state.p = p
    if moved then return "the character is moving" end
    return nil
end

local WALKS = { go = true, use = true, talk = true, loot = true }

-- Steps without progress: outside a fight and a dialogue, steps that end within AI.Stuck.radius
-- metres of where the count began. From AI.Stuck.steps of them on, every AI.Stuck.every-th step
-- tells it as a fact, with the ways the agent knows here that it has not used (AI.Tried).
AI.Stuck = AI.Stuck or { radius = 6, steps = 10, every = 5 }
AI.Tried = AI.Tried or {}
AI.Progress = AI.Progress or {}
function AI.NoProgress(state)
    local P, S = AI.Progress, AI.Stuck
    -- only the steps that move the party count: a window's presses (creation, a menu) stand still
    if not (state and state.args and (WALKS[state.args["do"]] or state.args["do"] == "jump")) then return nil end
    local me = U.controlled()
    local p = me and U.posOf(me)
    if not p or AI.Turn().on or U.inDialogue() then
        P.anchor, P.steps = p, 0
        return nil
    end
    if not P.anchor or U.dist(p, P.anchor) > S.radius then
        P.anchor, P.steps = p, 0
        return nil
    end
    P.steps = (P.steps or 0) + 1
    if P.steps < S.steps or (P.steps - S.steps) % S.every ~= 0 then return nil end
    -- the ways known here, nearest first, each marked when the agent has used it: going back is
    -- as much a way as going on
    local rows, lines = {}, {}
    for id, row in pairs(try(AI.Ways.Rows, AI.Limit) or {}) do rows[#rows + 1] = { id = id, row = row } end
    table.sort(rows, function(a, b) return (a.row.d or 0) < (b.row.d or 0) end)
    for i = 1, math.min(4, #rows) do lines[i] = rows[i].row.text .. (AI.Tried[rows[i].id] and " (used)" or "") end
    local open = try(AI.Ways.Open, me)
    return string.format("%d steps within %d m of %s,%s,%s with no new place reached; ways known here: %s%s",
        P.steps, S.radius, U.round1(P.anchor[1]), U.round1(P.anchor[2]), U.round1(P.anchor[3]),
        #lines > 0 and table.concat(lines, " | ") or "none", open and ("; open ground you see: " .. open) or "")
end

local function finish(state, final)
    if state.mark and AI.StepRunning == state.mark then AI.StepRunning = nil end
    local args, result = state.args, state.result or { ok = true }
    local out
    local window = type(result.result) == "table" and type(result.result.window) == "table" and result.result.window or nil
    if result.ok then
        local summary = type(result.result) == "table" and (result.result.summary or result.result.examined or (args["do"] == "ui" and result.result.saw)) or nil
        -- what the examine window showed that a fight turns on is kept for every creature of
        -- that name (AI.Examined), and told with it in can's enemies line
        if args["do"] == "examine" and type(result.result) == "table" and state.target then
            local facts = {}
            if result.result.ac then facts[#facts + 1] = "AC " .. tostring(result.result.ac) end
            if result.result.resist then facts[#facts + 1] = tostring(result.result.resist) end
            AI.Examined[tostring(nameOf(state.target))] = #facts > 0 and table.concat(facts, ", ") or "examined"
        end
        local did = tostring(state.did or "")
        -- a walk that ended short of its goal says where it ended, never "walked to" the goal
        if args["do"] == "go" and state.target and state.actor then
            local tp = type(state.target) == "table" and state.target or U.posOf(state.target)
            local mp = U.posOf(state.actor)
            if tp and mp then
                local dx, dy, dz = tp[1] - mp[1], tp[2] - mp[2], tp[3] - mp[3]
                local gap = math.sqrt(dx * dx + dz * dz)
                -- beside a creature but past a melee reach (1.5 m): the distance is said
                if gap > 1.6 and gap <= 2.5 and math.abs(dy) <= 2.5 and type(state.target) == "string" and try(Osi.IsCharacter, state.target) == 1 then
                    did = string.format("%s, %.1f m from it (a melee reach is 1.5 m)", did, gap)
                end
                if gap > 2.5 or math.abs(dy) > 2.5 then
                    did = string.format("walked toward %s and stopped %.1f m short of it%s at %.1f,%.1f,%.1f",
                        tostring(state.targetLabel), gap, math.abs(dy) > 2.5 and string.format(" (%.1f m %s)", math.abs(dy), dy > 0 and "below it" or "above it") or "",
                        mp[1], mp[2], mp[3])
                    -- why: the pathfinder's own answer from where it stopped, or that a walk does
                    -- reach it from here and this one was cut off (a line spoken, a surface, a fight)
                    -- in a fight a walk ends where the movement does: that is said first, and the
                    -- path is asked only when movement is left
                    local inFight = AI.Turn().on
                    local left = inFight and (((try(GK.See.Resources, state.actor) or {}).movement or { 0 })[1] or 0) or nil
                    local why, hint
                    if not inFight or left >= 1 then why, hint = try(AI.Ways.WhyNot, state.actor, state.target, "no path") end
                    if why then
                        did = did .. ": " .. why .. (hint and (" -> " .. hint) or "")
                    elseif inFight then
                        did = did .. (left < 0.5 and ": the movement of this turn is spent"
                            or string.format(": %.1f m of movement left this turn", left))
                    else
                        did = did .. ": a walk from here does reach it; this one was cut off - go again"
                    end
                end
            end
        end
        -- a jump or a cast that moves its caster (Jump, Misty Step, a teleport) at a point: said
        -- when the caster did not move
        local spell = tostring(state.spell or "")
        local moving = args["do"] == "jump"
        if args["do"] == "cast" and type(state.target) == "table" then
            for _, word in ipairs({ "Jump", "MistyStep", "Teleport", "DimensionDoor" }) do
                if spell:find(word, 1, true) then moving = true end
            end
        end
        if moving and state.startPos and state.actor then
            local now = U.posOf(state.actor)
            if now and U.dist(now, state.startPos) < 1 then
                did = did .. "; but " .. tostring(nameOf(state.actor)) .. " did not move (read now.events: an opportunity attack or a fall may have stopped it)"
            end
        end
        out = answer(true, did .. (summary and (": " .. tostring(summary)) or ""))
        if AI.Plan and AI.Plan.Did then pcall(AI.Plan.Did, args) end
    else
        local why, hint = result.error, state.hint or "read now.waiting and now.near, then try another act"
        -- a walk that ran out of time: where the character stands now
        if WALKS[args["do"]] and state.actor and type(why) == "string" and why:find("did not end", 1, true) then
            local mp = U.posOf(state.actor)
            local tp = state.target and (type(state.target) == "table" and state.target or U.posOf(state.target))
            if mp then
                why = why .. string.format("; the character stands at %.1f,%.1f,%.1f%s", mp[1], mp[2], mp[3],
                    tp and string.format(", %.1f m from %s", U.dist(mp, tp), tostring(state.targetLabel)) or "")
            end
        end
        -- GateKit names its own option; the agent's is the argument of go
        if type(why) == "string" then why = why:gsub("move with provoke = true", "do=go with provoke=true to the place") end
        -- the weapon attack aimed through the hotbar found no weapon action to aim: the agent's way
        -- is the action itself by its name
        if type(why) == "string" and why:find("GK.Gui.UseSlot", 1, true) then
            why = "the weapon attack could not be aimed from the hotbar"
            hint = "cast the attack by its name from now.can: do=cast spell=Target_MainHandAttack (melee) or Projectile_MainHandAttack (ranged) with the same target"
        end
        -- a walk the pathfinder refuses: why, and the way that leads there (AI.Ways)
        local pathWhy, pathHint
        if state.target and WALKS[args["do"]] then pathWhy, pathHint = try(AI.Ways.WhyNot, state.actor, state.target, result.error) end
        if pathWhy then
            why, hint = pathWhy, pathHint or hint
        else
            -- the game refused an act of the member's open turn: what is left, and what still fits
            local act = A[tostring(args["do"] or "")]
            if state.acted and act and act.turn and state.actor and not U.inDialogue() then
                local open = false
                for _, row in ipairs(AI.Turn().turn or {}) do
                    if row.mine and row.id == (uuidOf(state.actor) or state.actor) then open = true end
                end
                if open then why, hint = AI.TurnRefusal(state.actor, why) end
            end
        end
        out = answer(false, "", why, hint)
    end
    local now, chars = AI.Now()
    if final or state.capped then
        now.waiting = now.waiting or {}
        now.waiting[#now.waiting + 1] = string.format("the game is still busy (%s) after %d ticks -> do=wait", tostring(state.busy or "?"), state.ticks or 0)
    end
    if window then now.window = window end
    local stuck = AI.NoProgress(state)
    if stuck then
        now.waiting = now.waiting or {}
        now.waiting[#now.waiting + 1] = stuck
    end
    out.now = now
    local shown
    if args.say then shown = state.said == true end
    AI.Row("ai_step", { ["do"] = args["do"], target = state.targetLabel, spell = state.spell, n = args.n, who = state.actor and nameOf(state.actor) or nil,
        window = args.window, control = args.control, value = args.value,
        way = state.way, ok = out.ok, did = out.did ~= "" and out.did or nil, why_not = out.why_not ~= "" and out.why_not or nil,
        say = args.say, shown = shown, think = args.think, ticks = state.ticks or 0, ms = math.floor(Ext.Utils.MonotonicTime() - state.started), chars = chars })
    return out
end

--- One act, then the wait until the game has settled, then the answer.
---   args["do"]   the act, a name of AI.Acts: go, use, talk, loot, jump, cast, attack, end, select,
---                answer, continue, roll, take, close, wait, cheat_cast, cheat_teleport - or plan:
---                the act the loaded plan names for its step
---   args.target  what it is done to, as AI.Resolve reads it: a handle, a name, a point "x,y,z"
---   args.spell   for cast: the action's stats name, or its name or a part of either
---   args.n       for answer: the number
---   args.who     the party member who acts (default: in a fight the member whose turn is open,
---                else the controlled character)
---   args.say     a short line shown over the character's head, with the accent on the target
---   args.think   what the character thinks: kept in the row only
---   args.note    a note of the agent's own, kept with this place (AI.Notes)
--- Settled: the act has ended, and then either something waits for the player's answer, or for
--- AI.Quiet ticks in a row no line is spoken, no film plays, the game is not loading, in a fight
--- a party member's turn is open, and the controlled character does not move. A step waits at
--- most AI.Cap ticks; past that it answers with what still goes on, as a line of `waiting`.
--- Refused before anything is done: an unknown act, a missing argument, a target nobody knows, a
--- turn that is not the party's, and any act but an answering one while something waits.
--- Observed: { ok, did, why_not, hint, now } - `now` as AI.Now answers it. One row `ai_step`.
function AI.Step(args)
    args = type(args) == "table" and args or {}
    local state = { args = args, started = Ext.Utils.MonotonicTime() }
    local function refuse(why, hint)
        state.result, state.hint = { ok = false, error = why }, hint
        return finish(state)
    end
    local name = tostring(args["do"] or ""):lower()
    -- one step at a time: a call that comes while another still runs (its caller gave up waiting)
    -- would act on a world the first one is still changing
    local running = AI.StepRunning
    if running and Ext.Utils.MonotonicTime() - running.since < 150000 then
        -- a wait waits for it to end, then for the game to settle
        if name == "wait" then state.behind = true else return refuse("a step is still running: " .. tostring(running["do"]) .. ", for " .. math.floor((Ext.Utils.MonotonicTime() - running.since) / 1000) .. " s",
            call("ai_step", { ["do"] = "wait" }) .. " answers when it has ended") end
    end
    if name == "plan" then
        if not AI.Mode.Part("plan") then
            return refuse(AI.Mode.Refuse("a route plan"), "choose the act yourself from now.near and your goals")
        end
        local step = AI.Plan and AI.Plan.Step and AI.Plan.Step()
        if not (step and step["do"]) then
            return refuse("the plan names no act for this step", "read now.route (ai_look {\"part\":\"route\"}) and choose the act yourself")
        end
        args = { ["do"] = step["do"], target = step.target, spell = step.spell, n = step.n, who = step.who,
                 say = args.say, think = args.think, note = args.note }
        state.args, name = args, step["do"]
    end
    local act = A[name]
    if not act then
        return refuse("no such act: " .. (name == "" and "(none given)" or name), "do is one of: " .. actNames() .. ". For example " .. call("ai_step", { ["do"] = "wait" }))
    end
    state.way = act.way
    for _, need in ipairs(act.needs or {}) do
        if args[need] == nil or args[need] == "" then
            local example = { ["do"] = name, target = need == "target" and "<a handle from now.near>" or args.target,
                spell = need == "spell" and "<an action from now.can>" or args.spell, n = need == "n" and 1 or args.n,
                who = need == "who" and "<a party member's name>" or nil }
            return refuse(name .. " needs " .. need, call("ai_step", example))
        end
    end
    if AI.Think.required and (type(args.think) ~= "string" or args.think:match("^%s*$")) then
        local again = {}
        for k, v in pairs(args) do again[k] = v end
        again.think = "<why, in a few words>"
        return refuse("every step needs think: the reason for this act, in a few words (kept in the record, never shown)",
            call("ai_step", again))
    end
    -- a note of the place, or with `about` (a handle or a name) of that creature or thing
    if args.note then
        local about = args.about ~= nil and args.about ~= "" and AI.Resolve(args.about) or nil
        pcall(AI.Notes.Add, args.note, type(about) == "string" and about or nil)
    end
    -- the agent's goals, set or replaced with any step (Server/Goals.lua)
    if type(args.goals) == "string" then pcall(AI.SetGoals, args.goals, AI.Turn().mine or U.controlled()) end

    local waits = AI.Waiting()
    if waits.actionable and not act.answers then
        return refuse("something waits for an answer first: " .. tostring(waits.list[#waits.list]), "answer it as that line says, then repeat " .. call("ai_step", args))
    end
    -- a window that waits lies over the others: a press behind it is what a player cannot make
    if name == "ui" and args.control and waits.actionable and waits.widgets and not waits.widgets[args.window] then
        return refuse("a window waits over " .. tostring(args.window) .. ": " .. tostring(waits.list[#waits.list]),
            "answer it as that line says, then repeat " .. call("ai_step", args))
    end

    if act.guard then
        local why, hint = act.guard()
        if why then return refuse(why, hint) end
    end

    local turn = AI.Turn()
    local function text(v) return v ~= nil and v ~= "" and tostring(v) or nil end
    local c = { n = tonumber(args.n), window = text(args.window), control = text(args.control), value = text(args.value),
                provoke = args.provoke == true or args.provoke == "true" or nil }
    if args.who ~= nil and args.who ~= "" then
        local who = AI.Resolve(args.who)
        local member
        for _, m in ipairs(U.members()) do
            if type(who) == "string" and (uuidOf(m) or m) == (uuidOf(who) or who) then member = m end
        end
        if not member then return refuse("no party member is called " .. tostring(args.who), "who is a name from now.party") end
        c.actor = member
    else
        c.actor = turn.mine or U.controlled()
    end
    state.actor = c.actor
    -- a downed member's turn is the game's death saving throw: an act of it is refused before it
    -- goes anywhere, naming the members whose turn is open and can act
    if turn.on and act.turn and try(Osi.HasActiveStatus, c.actor, "DOWNED") == 1 then
        local others = {}
        for _, row in ipairs(turn.turn or {}) do
            if row.mine and row.id ~= (uuidOf(c.actor) or c.actor) and try(Osi.HasActiveStatus, row.id, "DOWNED") ~= 1 then
                others[#others + 1] = tostring(nameOf(row.id))
            end
        end
        return refuse(tostring(nameOf(c.actor)) .. " is downed: its turn is the game's death saving throw, which the game ends by itself",
            #others > 0 and name ~= "end" and call("ai_step", { ["do"] = name, target = args.target, spell = args.spell, who = others[1] })
                or call("ai_step", { ["do"] = "wait" }))
    end
    if turn.on and act.turn then
        local open
        for _, row in ipairs(turn.turn) do
            if row.id == (uuidOf(c.actor) or c.actor) then open = true end
        end
        if not open then
            return refuse(turn.mine and (tostring(nameOf(c.actor)) .. "'s turn is not open; the open turn is " .. tostring(nameOf(turn.mine)))
                or "it is not a party member's turn", turn.mine and call("ai_step", { ["do"] = name, target = args.target, spell = args.spell, who = nameOf(turn.mine) })
                or call("ai_step", { ["do"] = "wait" }))
        end
    end
    if args.target ~= nil and args.target ~= "" then
        local target, problem = AI.Resolve(args.target)
        if not target then
            return refuse(problem, "target is a handle from now.near (like #1a2b3c), a name, or a point \"x,y,z\"; " .. call("ai_look", { part = "near" }) .. " lists what is around")
        end
        if act.entity and type(target) ~= "string" then
            return refuse(name .. " needs a thing, not a point", call("ai_step", { ["do"] = name, target = "<a handle from now.near>" }))
        end
        c.target = target
        state.target, state.targetLabel = target, label(target)
    end
    if args.spell ~= nil and args.spell ~= "" then
        local spell, ready = spellOf(c.actor, args.spell)
        if not spell then
            return refuse(tostring(nameOf(c.actor)) .. " has no action called " .. tostring(args.spell),
                "spell is one of: " .. table.concat(ready or {}, ", "))
        end
        c.spell, state.spell = spell, spell
    end

    -- the line over the head is shown once the act has begun, never for an act that is refused
    local function sayIt()
        if state.said or not (args.say and args.say ~= "") then return end
        state.said = true
        local far = name == "go" or name == "use" or name == "talk" or name == "loot"
        pcall(function()
            GK.Unobserved(AI.Say({ say = args.say, think = args.think, ["do"] = name, at = c.target, who = c.actor,
                style = far and nil or { camera = false } }))
        end)
    end

    -- the member who acts is the one the player controls (GateKit's acts, scripted ones too, are
    -- given to the selected character only); the game's own flag decides - after a death or a
    -- fight's end it may hold nobody
    local selecting
    if not act.answers and name ~= "select" and try(Osi.IsControlled, c.actor) ~= 1 then
        selecting = follow(GK.Act.Select(c.actor))
    end
    local poll
    local function start()
        state.startPos = U.posOf(c.actor)
        local ok, a, b = pcall(act.run, c)
        if not ok then
            state.result = { ok = false, error = "the bridge failed: " .. tostring(a) }
            return
        end
        poll, state.did = a, b
        if poll == nil then state.result = { ok = true } end
    end
    if not state.behind then
        state.mark = { ["do"] = name, since = Ext.Utils.MonotonicTime() }
        AI.StepRunning = state.mark
    end
    if not selecting then start() end

    return GK.Observe({ ok = true, waiting = "the step" }, function(ticks, final)
        state.ticks = ticks
        local capped = final or ticks >= AI.Cap
        if state.behind and AI.StepRunning and not capped then return nil end
        if selecting then
            local s = selecting()
            if s == nil and not capped then return nil end
            selecting = nil
            if s and s.ok == false then state.result = s else state.selectedAt = ticks end
        end
        -- the client takes a few ticks to hold the newly controlled character whole: an aim
        -- written at once finds no place for its target
        if state.selectedAt then
            if ticks - state.selectedAt < 10 and not capped then return nil end
            state.selectedAt = nil
            start()
        end
        if state.result == nil then
            local r = poll()
            if r == nil and poll("begun") == true then sayIt() end
            if r == nil and not capped then return nil end
            state.result = r or { ok = false, error = "the act did not end in " .. ticks .. " ticks" }
            state.actedAt, state.acted = ticks, true
            if state.result.ok == false and state.result.detail and state.result.detail.hint then state.hint = tostring(state.result.detail.hint) end
        end
        if state.result.ok then sayIt() end
        state.actedAt = state.actedAt or ticks
        if ticks - state.actedAt < MIN and not capped then return nil end
        local why = busy(state)
        if why then state.calm, state.busy = 0, why else state.calm = (state.calm or 0) + 1 end
        if not why and AI.Waiting().actionable then return finish(state) end
        if state.calm and state.calm >= AI.Quiet then return finish(state) end
        if capped then
            state.capped = why ~= nil
            return finish(state)
        end
        return nil
    end, (AI.Cap + 30) * GK.TickMs)
end

-- ---------------------------------------------------------------------------------------
-- Look, scene, plan

local PARTS = { all = true, things = true, actions = true, notes = true, plan = true, prompt = true, thoughts = true }
for _, key in ipairs(AI.NowKeys) do PARTS[key] = true end

--- The whole picture (`part` left out or "all"), or one part: a key of `now` (where, fight, can,
--- party, waiting, near, heard, events, scenes, route, notes), or `things` (every object around,
--- items included), `actions` (what the acting member can do now), `notes` (every note of the
--- level), `plan` (every step of the loaded plan). args.detail: "brief" (default) or "full" - the
--- sizes of AI.Detail. The session's cursor moves to this picture. One row `ai_look`.
--- { ok, did, why_not, hint, now }.
function AI.Look(args)
    args = type(args) == "table" and args or {}
    local part = tostring(args.part or "all"):lower()
    -- a look at one part is asked for because the brief list left some of it out: it reads it whole
    local detail = (args.detail == "full" or (part ~= "all" and args.detail ~= "brief")) and "full" or "brief"
    local limits = AI.Detail[detail]
    local out
    if not PARTS[part] then
        local names = {}
        for name in pairs(PARTS) do names[#names + 1] = name end
        table.sort(names)
        out = answer(false, "", "no such part: " .. part, "part is one of: " .. table.concat(names, ", "))
        out.now = {}
        return out
    end
    if not AI.Mode.Part(part) then
        out = answer(false, "", AI.Mode.Refuse("the part " .. part), call("ai_look", { part = "all" }))
        out.now = {}
        return out
    end
    out = answer(true, "looked: " .. part)
    -- the reasons given with the steps: for a listener as much as for the agent, so the session's
    -- cursor stays and no row is written
    if part == "thoughts" then
        out.now = { thoughts = AI.Thoughts(limits.list) }
        return out
    end
    local prompt = AI.Session.prompt
    if part == "prompt" then
        out.prompt = prompt or ""
        out.now = {}
        if not prompt then out.did = "looked: no prompt is set" end
        AI.Row("ai_look", { part = part, detail = detail, chars = prompt and #prompt or 0 })
        return out
    end
    local now, chars = AI.Now({ full = true, detail = detail, wide = true })
    -- a look is what an agent does when it has lost the thread: it is reminded that a prompt stands
    if part == "all" and prompt then now.prompt = string.format("the owner's prompt stands (%d characters): %s reads it again", #prompt, call("ai_look", { part = "prompt" })) end
    if part == "things" then
        local list = {}
        local around = try(GK.See.Around, { radius = limits.radius, limit = limits.list * 3, scope = AI.Mode.Scope(false) }) or {}
        for _, o in ipairs(around.objects or {}) do
            if #list >= limits.list * 3 then break end
            list[#list + 1] = string.format("%s %s (%s), %s m %s", tostring(o.name or "something"), handle(o.id), tostring(o.kind), tostring(o.d), tostring(o.dir or ""))
        end
        now = { things = list }
    elseif part == "actions" then
        local list = {}
        local actor = AI.Turn().mine or U.controlled()
        for _, a in ipairs(try(GK.See.Actions, actor) or {}) do
            if a.ready and #list < limits.list * 3 then
                list[#list + 1] = tostring(a.id) .. (a.name and (" (" .. a.name .. ")") or "")
            end
        end
        now = { actions = list }
    elseif part == "can" then
        -- whole, never shaped to the block's cap
        local mine = AI.Turn().mine
        now = { can = mine and AI.Can(mine, limits) or {} }
    elseif part == "notes" then
        local list = {}
        local about = args.about ~= nil and args.about ~= "" and AI.Resolve(args.about) or nil
        if type(about) == "string" then
            -- the notes of one creature or thing, every one, the oldest first
            list = (AI.Notes.About(about) or { texts = {} }).texts
        else
            for _, note in ipairs(AI.Notes.Near(true)) do list[#list + 1] = note.text end
        end
        now = { notes = list }
    elseif part == "plan" then
        local list = {}
        local plan = AI.Plan.Current
        for i, step in ipairs(plan and plan.steps or {}) do
            list[#list + 1] = string.format("%s%d %s", i == AI.Plan.Index and "> " or "", i, step.goal)
        end
        now = { plan = list }
    elseif part ~= "all" then
        now = { [part] = now[part] }
        if now[part] == nil then now[part] = {} end
        -- the ways that are no thing: open ground the party sees, by bearing (AI.Ways.Open)
        if part == "ways" then
            local open = try(AI.Ways.Open, U.controlled())
            if open then table.insert(now.ways, "open ground you see: " .. open) end
        end
    end
    if part ~= "all" then chars = #AI.Encode(now, AI.NowKeys) end
    out.now = now
    AI.Row("ai_look", { part = part, detail = detail, chars = chars })
    return out
end

local function sceneOf(name)
    local scenes = try(AI.SceneTable)
    return type(scenes) == "table" and type(scenes[name]) == "table" and scenes[name] or nil
end

-- `arm` and `off` of a scene of the stand's manifest (Shared/Stand.lua): the code the host read
-- from `file` (args.code), else the code kept at the last arming.
local function standCall(name, fn, args)
    local St = AI.Stand
    local entry = St.Entry(name)
    if not entry then
        local stand = St.Current()
        local out = answer(false, "", stand and ("the stand's manifest has no scene " .. tostring(name)) or "no stand's manifest is given",
            stand and call("ai_scene", {}) .. " lists its scenes" or 'ai_session {"scenes_file":"<the manifest>"} gives it')
        out.now = {}
        return out
    end
    local code = type(args.code) == "string" and args.code ~= "" and args.code or St.Code(name, fn)
    if not code then
        local out = answer(false, "", "the code of " .. name .. "'s " .. fn .. " is not kept yet: name its file once", St.Hint(name, fn))
        out.now = {}
        return out
    end
    if args.code then St.Keep(name, fn, code) end
    local step = GK.Follow(AI.StandCall(name, fn, code))
    return GK.Observe({ ok = true, waiting = "the scene" }, function()
        local r = step()
        if r == nil then return nil end
        local out = r.ok and answer(true, r.did) or answer(false, "", r.error, "tell the owner")
        out.now = AI.Now()
        return out
    end, 8000)
end

-- A call of a scene under the client's GKScene: run there, in the UI's context (AI.SceneCall).
local function clientCall(name, fn)
    local got
    AI.Ask({ act = "call", scene = name, call = fn }, function(a) got = a end)
    return GK.Observe({ ok = true, waiting = "the scene" }, function(_, final)
        if got == nil and not final then return nil end
        got = got or { ok = false, error = "the client did not answer" }
        local out = got.ok == false and answer(false, "", cut(tostring(got.error or "refused"), 400), "read the scene's state: " .. call("ai_scene", { name = name }))
            or answer(true, name .. "." .. fn .. "()" .. (type(got.answered) == "string" and (" answered " .. got.answered) or ""))
        AI.Row("ai_scene", { name = name, call = fn, ok = out.ok, error = out.why_not ~= "" and out.why_not or nil })
        out.now = AI.Now()
        return out
    end, 30000)
end

-- The manifest's scenes that are not under GKScene now: each with how it is armed.
local function standLines(lines, clientLines)
    local out = {}
    local stand = AI.Stand.Current()
    if not stand then return out end
    local armed = AI.Stand.Armed()
    for name, entry in pairs(stand.scenes) do
        if not lines[name] and not clientLines[name] then
            local how = AI.Stand.Code(name, "arm") and call("ai_scene", { name = name, call = "arm" }) or AI.Stand.Hint(name, "arm")
            local on = armed[name] and (armed[name].level == "" and "the menu" or tostring(armed[name].level))
            out[#out + 1] = name .. " (" .. tostring(entry.side) .. "): " .. (on and ("armed on " .. on .. ", no table under GKScene") or "not armed") .. " | arm: " .. how
        end
    end
    if AI.Checkpoint then
        out[#out + 1] = string.format("checkpoint: %s when %s%s", AI.Checkpoint.scene, AI.Checkpoint.flag, AI.Checkpoint.ok and "" or " (the game made no save)")
    end
    return out
end

--- A scene: any table under the global GKScene of GateKit's Lua environment. Without `name`:
--- every scene with its state, and the stand's scenes not armed with the call that arms each.
--- With `name`: its state and the calls it offers. With `call`: that function of the scene is
--- run, with no arguments, and what it answered is said (AI.SceneSaid); `arm` and `off` arm and
--- disarm a scene of the stand's manifest, from `file` (args.code: the host read it) or from the
--- code kept. A scene of the client's is listed with its state, and its call is run on the client,
--- in the UI's context. One row `ai_scene`. { ok, did, why_not, hint, now } with `now.scenes`.
function AI.Scene(args)
    args = type(args) == "table" and args or {}
    local name, fn = args.name, args.call
    if type(name) == "string" and name:find(".", 1, true) and not fn then name, fn = name:match("^([^%.]+)%.(.+)$") end
    if type(fn) == "string" then fn = fn:gsub("%(%)$", "") end
    if (fn == "arm" or fn == "off") and type(name) == "string" and name ~= "" then
        return standCall(name, fn, args)
    end
    local lines = AI.SceneLines(Mods.GateKit)
    local clientLines = AI.Client.scenes or {}
    if type(fn) == "string" and fn ~= "" and type(name) == "string" and not sceneOf(name) and clientLines[name] then
        return clientCall(name, fn)
    end
    local out
    -- blind: a scene's state is the director's. A call the owner names is still run; nothing of
    -- the scene is read back
    if not AI.Mode.Part("scenes") then
        local scene = type(name) == "string" and sceneOf(name) or nil
        if not (scene and type(fn) == "string" and type(scene[fn]) == "function") then
            out = answer(false, "", AI.Mode.Refuse("the scenes and their state"), "a call the owner names is run with " .. call("ai_scene", { name = "<scene>", call = "<call>" }))
            out.now = {}
            return out
        end
        local ok, result = pcall(scene[fn])
        if not ok then
            out = answer(false, "", name .. "." .. fn .. "() failed: " .. cut(tostring(result), 160), "tell the owner")
        elseif type(result) == "table" and result.ok == false then
            -- the scene refused: not now, and why; nothing was done
            out = answer(false, "", name .. "." .. fn .. "(): " .. cut(tostring(result.error or "refused"), 160),
                result.hint and cut(tostring(result.hint), 160) or "read the scene's state in now.scenes")
        else
            out = answer(true, name .. "." .. fn .. "()")
        end
        AI.Row("ai_scene", { name = name, call = fn, ok = out.ok, error = out.why_not ~= "" and out.why_not or nil })
        out.now = AI.Now()
        return out
    end
    if name == nil or name == "" then
        local list = {}
        for scene, line in pairs(lines) do list[#list + 1] = scene .. ": " .. line .. " | calls: " .. table.concat(AI.SceneCalls(sceneOf(scene)), ", ") end
        for scene, line in pairs(clientLines) do if not lines[scene] then list[#list + 1] = scene .. " (client): " .. line end end
        for _, line in ipairs(standLines(lines, clientLines)) do list[#list + 1] = line end
        table.sort(list)
        out = answer(true, "listed the scenes")
        out.now = { scenes = list }
        return out
    end
    local scene = sceneOf(name)
    if not scene then
        local known = {}
        for scene in pairs(lines) do known[#known + 1] = scene end
        table.sort(known)
        out = answer(false, "", clientLines[name] and ("the scene " .. name .. " lives on the client: its calls are the driver's")
            or ("no scene is called " .. tostring(name)), #known > 0 and ("name is one of: " .. table.concat(known, ", ")) or "no scene is armed")
        out.now = { scenes = clientLines[name] and { name .. " (client): " .. clientLines[name] } or {} }
        return out
    end
    local calls = AI.SceneCalls(scene)
    if fn == nil or fn == "" then
        out = answer(true, "read the scene " .. name)
        out.now = { scenes = { name .. ": " .. AI.SceneLine(scene) .. " | calls: " .. table.concat(calls, ", ") } }
        return out
    end
    if type(scene[fn]) ~= "function" then
        out = answer(false, "", "the scene " .. name .. " has no call " .. tostring(fn), "call is one of: " .. table.concat(calls, ", "))
        out.now = { scenes = { name .. ": " .. AI.SceneLine(scene) } }
        return out
    end
    local ok, result = pcall(scene[fn])
    if ok then
        local fine, said, why = AI.SceneSaid(result)
        out = answer(fine, fine and (name .. "." .. fn .. "()" .. (said and (" answered " .. said) or "")) or "",
            why, why and (type(result) == "table" and result.hint and tostring(result.hint) or "read the scene's state in now.scenes") or nil)
    else
        out = answer(false, "", name .. "." .. fn .. "() failed: " .. cut(tostring(result), 160), "tell the owner")
    end
    AI.Row("ai_scene", { name = name, call = fn, ok = out.ok, error = out.why_not ~= "" and out.why_not or nil })
    out.now = AI.Now()
    if not out.now.scenes then out.now.scenes = { name .. ": " .. AI.SceneLine(scene) } end
    return out
end

--- The session's start, and its reading: AI.Session.Start with `args` (mode, prompt, prompt_from,
--- plan, step; with none of them the session is read and nothing changes), answered with the
--- owner's prompt as `prompt` and the whole picture as `now`. One row `ai_session` when it began.
--- { ok, mode, did, why_not, hint, prompt, now }.
function AI.SessionCall(args)
    args = type(args) == "table" and args or {}
    local begins = false
    for _, k in ipairs({ "mode", "prompt", "scenes", "plan", "step" }) do if args[k] ~= nil then begins = true end end
    local result = begins and AI.Session.Start(args) or { ok = true }
    local out = result.ok and answer(true, begins and ("the session began in mode " .. AI.Mode.name) or "read the session")
        or answer(false, "", result.error, result.hint)
    out.prompt = AI.Session.prompt or ""
    out.now = AI.Now({ full = true, wide = true })
    return out
end

local TOOLS = { step = "Step", look = "Look", scene = "Scene", session = "SessionCall" }

--- One of the four tools - "session", "step", "look", "scene" - as the text a tool answers: one
--- line of JSON whose keys are always ok, mode, did, why_not, hint, now, in that order, with
--- `prompt` before `now` where the tool gives it (AI.Encode). Observed when the tool is (a step).
function AI.Tool(name, args)
    local fn = AI[TOOLS[name] or ""]
    if not fn then return AI.Encode(answer(false, "", "no such tool: " .. tostring(name), "the tools are ai_session, ai_step, ai_look, ai_scene"), ORDER) end
    local ok, value = pcall(fn, args)
    if not ok then
        local failed = answer(false, "", "the bridge failed: " .. cut(tostring(value), 200), "tell the owner; " .. call("ai_look", {}) .. " reads the game")
        failed.now = {}
        return AI.Encode(failed, ORDER)
    end
    local plain, check, within = GK.Unobserved(value)
    if not check then return AI.Encode(plain, ORDER) end
    local step = GK.Follow(value)
    return GK.Observe({ ok = true, waiting = "the " .. name }, function()
        local a = step()
        if a == nil then return nil end
        if type(a) ~= "table" then return tostring(a) end
        a.waitedTicks, a.waitedMs = nil, nil
        if a.now == nil then
            -- the bridge's own limit ran out: GK.Follow's words
            local late = answer(false, "", tostring(a.error or "the step did not answer"), call("ai_step", { ["do"] = "wait" }))
            late.now = AI.Now()
            return AI.Encode(late, ORDER)
        end
        return AI.Encode(a, ORDER)
    end, (within or 1500) + 3000)
end
