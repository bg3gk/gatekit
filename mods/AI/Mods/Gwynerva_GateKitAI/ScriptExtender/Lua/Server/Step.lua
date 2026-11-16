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
    for _, k in ipairs({ "do", "target", "spell", "n", "who", "part", "name", "call" }) do
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
local function follow(result)
    local step = GK.Follow(result)
    local final
    return function()
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
    return function()
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
    run = function(c) return follow(GK.Cheat.Move(c.actor, c.target, { partial = true })), "walked to " .. label(c.target) end }
A.use = { way = "ordered", needs = { "target" }, entity = true, turn = true, about = "walk up and use: a door, a lever, a ladder, a pod",
    run = function(c) return follow(GK.Cheat.Use(c.actor, c.target)), "used " .. label(c.target) end }
A.talk = { way = "ordered", needs = { "target" }, entity = true, about = "walk up and talk",
    run = function(c) return follow(GK.Cheat.Talk(c.actor, c.target)), "asked " .. label(c.target) .. " to talk" end }
A.loot = { way = "ordered", needs = { "target" }, entity = true, about = "walk up and open a body or a container",
    run = function(c) return follow(GK.Cheat.Loot(c.actor, c.target)), "opened " .. label(c.target) end }
A.jump = { way = "ordered", needs = { "target" }, turn = true, about = "jump to a point",
    run = function(c) return follow(GK.Cheat.Jump(c.actor, type(c.target) == "table" and c.target or U.posOf(c.target))), "jumped to " .. label(c.target) end }
A.cast = { way = "fair", needs = { "spell", "target" }, turn = true, select = true, about = "cast a spell or use an action at a thing or a point",
    run = function(c) return client({ act = "cast", spell = c.spell, target = c.target }), "cast " .. c.spell .. " at " .. label(c.target) end }
A.attack = { way = "fair", needs = { "target" }, entity = true, turn = true, select = true, about = "the weapon attack",
    run = function(c) return client({ act = "attack", target = c.target }), "attacked " .. label(c.target) end }
A["end"] = { way = "fair", turn = true, about = "end the turn",
    run = function(c) return follow(GK.Act.EndTurn(c.actor)), "ended the turn of " .. tostring(nameOf(c.actor)) end }
A.select = { way = "fair", needs = { "who" }, about = "take control of a party member",
    run = function(c) return follow(GK.Act.Select(c.actor)), "took control of " .. tostring(nameOf(c.actor)) end }
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
A.close = { way = "fair", answers = true, about = "close the window that waits",
    run = function() return client({ act = "resolve", policy = CLOSE, never = { dialogue = true, roll = true } }), "closed the window" end }
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

local function finish(state, final)
    local args, result = state.args, state.result or { ok = true }
    local out
    if result.ok then
        local summary = type(result.result) == "table" and result.result.summary or nil
        out = answer(true, tostring(state.did or "") .. (summary and (": " .. tostring(summary)) or ""))
        if AI.Plan and AI.Plan.Did then pcall(AI.Plan.Did, args) end
    else
        out = answer(false, "", result.error, state.hint or "read now.waiting and now.near, then try another act")
    end
    local now, chars = AI.Now()
    if final or state.capped then
        now.waiting = now.waiting or {}
        now.waiting[#now.waiting + 1] = string.format("the game is still busy (%s) after %d ticks -> do=wait", tostring(state.busy or "?"), state.ticks or 0)
    end
    out.now = now
    AI.Row("ai_step", { ["do"] = args["do"], target = state.targetLabel, spell = state.spell, n = args.n, who = state.actor and nameOf(state.actor) or nil,
        way = state.way, ok = out.ok, did = out.did ~= "" and out.did or nil, why_not = out.why_not ~= "" and out.why_not or nil,
        say = args.say, think = args.think, ticks = state.ticks or 0, ms = math.floor(Ext.Utils.MonotonicTime() - state.started), chars = chars })
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
    if args.note then pcall(AI.Notes.Add, args.note) end

    local waits = AI.Waiting()
    if waits.actionable and not act.answers then
        return refuse("something waits for an answer first: " .. tostring(waits.list[#waits.list]), "answer it as that line says, then repeat " .. call("ai_step", args))
    end

    if act.guard then
        local why, hint = act.guard()
        if why then return refuse(why, hint) end
    end

    local turn = AI.Turn()
    local c = { n = tonumber(args.n) }
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
        state.targetLabel = label(target)
    end
    if args.spell ~= nil and args.spell ~= "" then
        local spell, ready = spellOf(c.actor, args.spell)
        if not spell then
            return refuse(tostring(nameOf(c.actor)) .. " has no action called " .. tostring(args.spell),
                "spell is one of: " .. table.concat(ready or {}, ", "))
        end
        c.spell, state.spell = spell, spell
    end

    if args.say and args.say ~= "" then
        local far = name == "go" or name == "use" or name == "talk" or name == "loot"
        pcall(function()
            GK.Unobserved(AI.Say({ say = args.say, think = args.think, ["do"] = name, at = c.target, who = c.actor,
                style = far and nil or { camera = false } }))
        end)
    end

    -- the member who acts is the one the player controls, where the act is the player's own click
    local selecting
    if act.select and (uuidOf(c.actor) or c.actor) ~= (uuidOf(U.controlled()) or U.controlled()) then
        selecting = follow(GK.Act.Select(c.actor))
    end
    local poll
    local function start()
        local ok, a, b = pcall(act.run, c)
        if not ok then
            state.result = { ok = false, error = "the bridge failed: " .. tostring(a) }
            return
        end
        poll, state.did = a, b
        if poll == nil then state.result = { ok = true } end
    end
    if not selecting then start() end

    return GK.Observe({ ok = true, waiting = "the step" }, function(ticks, final)
        state.ticks = ticks
        local capped = final or ticks >= AI.Cap
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
            if r == nil and not capped then return nil end
            state.result = r or { ok = false, error = "the act did not end in " .. ticks .. " ticks" }
            state.actedAt = ticks
            if state.result.ok == false and state.result.detail and state.result.detail.hint then state.hint = tostring(state.result.detail.hint) end
        end
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

local PARTS = { all = true, things = true, actions = true, notes = true, plan = true, prompt = true }
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
    local detail = args.detail == "full" and "full" or "brief"
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
    elseif part == "notes" then
        local list = {}
        for _, note in ipairs(AI.Notes.Near(true)) do list[#list + 1] = note.text end
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
    end
    if part ~= "all" then chars = #AI.Encode(now, AI.NowKeys) end
    out.now = now
    AI.Row("ai_look", { part = part, detail = detail, chars = chars })
    return out
end

local function sceneOf(name)
    local scenes = try(function() return Mods.GateKit.GKScene end)
    return type(scenes) == "table" and type(scenes[name]) == "table" and scenes[name] or nil
end

--- A scene: any table under the global GKScene of GateKit's Lua environment. Without `name`:
--- every scene with its state. With `name`: its state and the calls it offers. With `call`: that
--- function of the scene is run, with no arguments, and what it answered is said. A scene of the
--- client's is listed with its state; its calls are not reachable from here. One row `ai_scene`.
--- { ok, did, why_not, hint, now } with `now.scenes`.
function AI.Scene(args)
    args = type(args) == "table" and args or {}
    local name, fn = args.name, args.call
    if type(name) == "string" and name:find(".", 1, true) and not fn then name, fn = name:match("^([^%.]+)%.(.+)$") end
    if type(fn) == "string" then fn = fn:gsub("%(%)$", "") end
    local lines = AI.SceneLines(Mods.GateKit)
    local clientLines = AI.Client.scenes or {}
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
        out = ok and answer(true, name .. "." .. fn .. "()") or answer(false, "", name .. "." .. fn .. "() failed: " .. cut(tostring(result), 160), "tell the owner")
        AI.Row("ai_scene", { name = name, call = fn, ok = out.ok, error = out.why_not ~= "" and out.why_not or nil })
        out.now = AI.Now()
        return out
    end
    if name == nil or name == "" then
        local list = {}
        for scene, line in pairs(lines) do list[#list + 1] = scene .. ": " .. line .. " | calls: " .. table.concat(AI.SceneCalls(sceneOf(scene)), ", ") end
        for scene, line in pairs(clientLines) do if not lines[scene] then list[#list + 1] = scene .. " (client): " .. line end end
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
        local said = type(result) == "table" and (result.error or result.why or (result.ok == false and "refused")) or nil
        out = answer(not said, name .. "." .. fn .. "()" .. ((type(result) == "string" or type(result) == "number" or type(result) == "boolean") and (" answered " .. tostring(result)) or ""),
            said and tostring(said) or nil, said and "read the scene's state in now.scenes" or nil)
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
    for _, k in ipairs({ "mode", "prompt", "plan", "step" }) do if args[k] ~= nil then begins = true end end
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
