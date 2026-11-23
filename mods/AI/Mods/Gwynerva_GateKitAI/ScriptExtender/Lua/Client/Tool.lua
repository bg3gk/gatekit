-- AI.Tool (client side): the door every tool call comes in by (tools/ai.json runs it on the
-- client). While a level runs, the server answers: the call goes over the mod's channel and its
-- answer comes back unchanged. Before a level is loaded - the splash screen, the main menu, a
-- film, a load - the game has no server, and the client answers itself with what a player has
-- there: the acts `ui`, `continue`, `close` and `wait`, the stand's scenes of the client and the
-- UI, the session's start, and a `now` of what holds the game (`where`, `waiting`, `scenes`).

local GK = AI.GK
local St = AI.Stand
local ORDER = { "ok", "mode", "did", "why_not", "hint", "prompt", "now" }
local QUIET, CAP = 8, 900     -- ticks of an unchanged screen after which a step answers; its limit

local function clock() return Ext.Utils.MonotonicTime() end
local function gameState()
    local ok, s = pcall(function() return tostring(Ext.Utils.GetGameState()) end)
    return ok and s or "?"
end

--- Whether the server runs and hears the client: its hello is fresh. (The server of a new game
--- runs while the client still shows the film.)
function AI.ServerUp()
    return AI.ServerSeen ~= nil and clock() - AI.ServerSeen < 3000
end

local function mode()
    local m = AI.SessionFile.Read().mode
    return (m == "blind" or m == "open") and m or "open"
end

local function answer(ok, did, why, hint)
    return { ok = ok, mode = mode(), did = did or "", why_not = why or "", hint = hint or "" }
end

local function encoded(out)
    out.now = out.now or {}
    return AI.Encode(out, ORDER)
end

-- ---------------------------------------------------------------------------------------
-- In a level: the server answers

AI.NextForward = AI.NextForward or 0

local function forward(name, args)
    AI.NextForward = AI.NextForward + 1
    local id = AI.NextForward
    if not AI.ToServer({ tool = name, args = args, forward = id }) then
        return encoded(answer(false, "", "the server could not be reached", '{"do":"wait"} and call again'))
    end
    return GK.Observe({ ok = true, waiting = "the server's answer" }, function(_, final)
        local text = AI.Replies[id]
        if text ~= nil then
            AI.Replies[id] = nil
            return text
        end
        -- the level was left while the call ran (the server's Lua ends with it): its answer is lost.
        -- A server busy with a long step may miss its hello for seconds while the game still runs:
        -- only the game's own state leaving Running says the level is gone
        if not AI.ServerUp() and gameState() ~= "Running" then
            local out = answer(true, "the call was made and the level is being left: its answer is lost with the level")
            out.now = AI.ClientNow(true)
            return encoded(out)
        end
        if final then return encoded(answer(false, "", "the server did not answer in time", 'ai_step {"do":"wait"}')) end
    end, 110000)
end

-- ---------------------------------------------------------------------------------------
-- No level: the client's own `now`

local told = {}   -- what the agent was last told here: where, waiting, scenes

local function holding()
    local ok, stuck, screen = pcall(GK.Gui.Stuck)
    if not ok then return nil end
    return stuck, screen
end


local WHERE = { splash = "the splash screen", mainmenu = "the main menu", film = "a film plays",
                gameover = "the game-over screen", firstlaunch = "a first-launch screen" }

-- The window that waits over the others (GK.Gui.Blocking: a message box, a tutorial ...), as
-- { window, widget, line }: its line ends with the step that answers it.
local function blocker()
    local ok, b = pcall(GK.Gui.Blocking)
    if not ok or type(b) ~= "table" or not b.window then return nil end
    local s = type(b.summary) == "table" and b.summary or {}
    local text = table.concat({ s.title or "", s.text or s.message or "" }, s.title and (s.text or s.message) and ": " or "")
    if #text > 300 then
        local last = 297
        while last > 0 and text:byte(last + 1) and text:byte(last + 1) >= 0x80 and text:byte(last + 1) < 0xC0 do last = last - 1 end
        text = text:sub(1, last) .. "..."
    end
    local buttons = {}
    for _, button in ipairs(type(s.buttons) == "table" and s.buttons or {}) do
        buttons[#buttons + 1] = tostring(button.n) .. ") " .. tostring(button.text)
    end
    local how = #buttons > 0 and "do=answer n=<number>" or "do=close"
    return { window = b.window, widget = b.widget,
             line = string.format("a %s window: %s%s -> %s", tostring(b.window), text, #buttons > 0 and (" " .. table.concat(buttons, " ")) or "", how) }
end

local function picture()
    local stuck, screen = holding()
    local state = gameState()
    local where = (WHERE[stuck or ""] or "no level is loaded") .. " (game state " .. state .. ")"
    local waiting = {}
    if stuck == "splash" then
        waiting[#waiting + 1] = "the splash screen waits for a key -> do=continue"
    elseif stuck == "film" then
        waiting[#waiting + 1] = "a film plays -> do=close skips it, do=wait watches it"
    elseif stuck == "gameover" then
        waiting[#waiting + 1] = "the game is over: the party is dead. Do nothing; tell the owner"
    elseif stuck == "firstlaunch" then
        waiting[#waiting + 1] = "a first-launch screen -> do=ui window=" .. tostring(screen and screen.widget) .. " lists its controls"
    end
    local windows = stuck ~= "splash" and stuck ~= "film" and AI.OpenWindows() or {}
    if #windows > 0 then
        waiting[#waiting + 1] = "open windows: " .. table.concat(windows, ", ") .. " -> do=ui window=<one of them> lists its controls"
    end
    local b = stuck ~= "gameover" and blocker() or nil
    if b then waiting[#waiting + 1] = b.line end
    local scenes = {}
    if mode() == "open" then
        for name, line in pairs(AI.SceneLines(Mods.GateKit)) do scenes[#scenes + 1] = name .. ": " .. line end
        table.sort(scenes)
    end
    return { where = where, waiting = waiting, scenes = scenes }
end

local function same(a, b)
    if type(a) ~= "table" or #a ~= #b then return false end
    for i = 1, #b do if a[i] ~= b[i] then return false end end
    return true
end

-- The block: what changed since the agent was last told (`full`: all of it).
local function nowBlock(full)
    local p = picture()
    local now = {}
    if full or p.where ~= told.where then now.where = p.where end
    if full or not same(told.waiting, p.waiting) then now.waiting = p.waiting end
    if (full or not same(told.scenes, p.scenes)) and #p.scenes > 0 then now.scenes = p.scenes end
    told = { where = p.where, waiting = p.waiting, scenes = p.scenes }
    return now
end
AI.ClientNow = nowBlock

local function noLevel(what)
    return "no level is loaded (game state " .. gameState() .. "): " .. what
end

-- ---------------------------------------------------------------------------------------
-- No level: the tools

-- Carries out an act here and answers when it has ended and the screen has stood still.
local function carried(act, did)
    local got = act == nil and { ok = true } or nil
    if act then AI.Carry(act, function(a) got = a end) end
    local doneAt, last, calm
    return GK.Observe({ ok = true, waiting = "the step" }, function(ticks, final)
        if got == nil and not final then return nil end
        doneAt = doneAt or ticks
        local sig = gameState() .. "|" .. tostring((holding())) .. "|" .. table.concat(AI.OpenWindows(), ",")
        if sig == last then calm = (calm or 0) + 1 else last, calm = sig, 0 end
        if calm < QUIET and ticks - doneAt < CAP and not final then return nil end
        got = got or { ok = false, error = "the act did not end" }
        local out
        if got.ok == false then
            out = answer(false, "", tostring(got.error or got.saw or "refused"), "read now.waiting")
        else
            local extra = got.saw or got.summary
            out = answer(true, did .. (extra and (": " .. tostring(extra)) or ""))
        end
        out.now = nowBlock(false)
        if type(got.window) == "table" then out.now.window = got.window end
        return encoded(out)
    end, (CAP + 120) * GK.TickMs)
end

local function step(args)
    local name = tostring(args["do"] or ""):lower()
    local stuck, screen = holding()
    local function refuse(why, hint)
        local out = answer(false, "", why, hint)
        out.now = nowBlock(false)
        return encoded(out)
    end
    if name == "ui" then
        local function text(v) return v ~= nil and v ~= "" and tostring(v) or nil end
        local window, control, value = text(args.window), text(args.control), text(args.value)
        if not window then return refuse("ui needs window", 'ai_step {"do":"ui","window":"<a window now.waiting names>"}') end
        -- a window that waits lies over the others: a press behind it is what a player cannot make
        local b = control and blocker() or nil
        if b and b.widget ~= window then
            return refuse("a window waits over " .. window .. ": " .. b.line, "answer it as that line says, then repeat the press")
        end
        local did = (control == nil and "looked at " or (value ~= nil and "set " or "clicked ")) .. window
            .. (control and ("/" .. control) or "") .. (value and (control and (" = " .. value) or (" for " .. value)) or "")
        return carried({ act = "ui", window = window, control = control, value = value }, did)
    elseif name == "continue" then
        if stuck ~= "splash" then return refuse(noLevel("nothing waits to go on"), "read now.waiting") end
        return carried({ act = "event", event = screen.event, widget = screen.widget }, "pressed a key on the splash screen")
    elseif name == "answer" then
        local b = blocker()
        if not b then return refuse(noLevel("nothing waits for an answer"), "read now.waiting") end
        local n = tonumber(args.n)
        if not n then return refuse("answer needs n", 'ai_step {"do":"answer","n":1}') end
        return carried({ act = "resolve", only = { box = true }, policy = { box = n } }, "chose answer " .. n)
    elseif name == "close" then
        if stuck ~= "film" then
            if blocker() then
                return carried({ act = "resolve", policy = { box = "negative", tutorial = "close" }, never = { gameover = true } }, "closed the window")
            end
            return refuse(noLevel("no film plays and no window waits to be closed"), "a window is used with do=ui")
        end
        return carried({ act = "key", key = "ESCAPE" }, "skipped the film")
    elseif name == "wait" then
        return carried(nil, "waited")
    end
    return refuse(noLevel(name == "" and "no act is given" or (name .. " needs one")), "here do is ui, continue, answer, close or wait, as now.waiting says")
end

local function look(args)
    local part = tostring(args.part or "all"):lower()
    local out = answer(true, "looked: " .. part)
    if part == "prompt" then
        out.prompt = AI.SessionFile.Read().prompt or ""
        return encoded(out)
    end
    local now = nowBlock(true)
    if part ~= "all" then
        if now[part] == nil then return encoded(answer(false, "", noLevel("the part " .. part .. " is read in a level"), 'ai_look {"part":"all"}')) end
        now = { [part] = now[part] }
    end
    out.now = now
    return encoded(out)
end

local function session(args)
    if (args.plan ~= nil and args.plan ~= "") or args.step ~= nil then
        return encoded(answer(false, "", noLevel("a plan is loaded in a level"), "call ai_session with plan again once a level runs"))
    end
    local fields = {}
    if args.mode ~= nil and args.mode ~= "" then
        if args.mode ~= "open" and args.mode ~= "blind" then return encoded(answer(false, "", "no such mode: " .. tostring(args.mode), "mode is open or blind")) end
        fields.mode = args.mode
    end
    if args.prompt ~= nil then
        local text = tostring(args.prompt)
        if #text > 16000 then return encoded(answer(false, "", "the prompt is " .. #text .. " bytes; the most is 16000", "shorten it")) end
        fields.prompt = text ~= "" and text or false
        fields.promptFrom = text ~= "" and args.prompt_from or false
    end
    if args.scenes ~= nil then
        local stand, problem = St.Take(args.scenes, args.scenes_from)
        if not stand then return encoded(answer(false, "", problem, "scenes_file is the path of the stand's manifest of scenes (JSON)")) end
    end
    local begins = next(fields) ~= nil or args.scenes ~= nil
    if next(fields) then AI.SessionFile.Write(fields) end
    local out = answer(true, begins and ("the session began in mode " .. mode()) or "read the session")
    out.prompt = AI.SessionFile.Read().prompt or ""
    out.now = nowBlock(true)
    return encoded(out)
end

local function scene(args)
    local name, fn = args.name, args.call
    if type(fn) == "string" then fn = fn:gsub("%(%)$", "") end
    local blind = mode() == "blind"
    local function refuse(why, hint)
        local out = answer(false, "", why, hint)
        return encoded(out)
    end
    if name == nil or name == "" then
        if blind then return refuse("not knowable to a player: the scenes and their state", 'a call the owner names is run with ai_scene {"name":"<scene>","call":"<call>"}') end
        local list = {}
        local lines = AI.SceneLines(Mods.GateKit)
        for n, line in pairs(lines) do list[#list + 1] = n .. ": " .. line end
        local stand = St.Current()
        for n, e in pairs(stand and stand.scenes or {}) do
            if not lines[n] then
                local how = e.side == "server" and "in a level" or (St.Code(n, "arm") and string.format('ai_scene {"name":"%s","call":"arm"}', n) or St.Hint(n, "arm"))
                list[#list + 1] = n .. " (" .. tostring(e.side) .. "): not armed | arm: " .. how
            end
        end
        table.sort(list)
        local out = answer(true, "listed the scenes")
        out.now = { scenes = list }
        return encoded(out)
    end
    if fn == "arm" or fn == "off" then
        local e = St.Entry(name)
        if not e then return refuse(St.Current() and ("the stand's manifest has no scene " .. name) or "no stand's manifest is given", 'ai_session {"scenes_file":"<the manifest>"}') end
        if e.side == "server" then return refuse(noLevel(name .. " is a scene of the server"), "arm it once a level runs") end
        local code = type(args.code) == "string" and args.code ~= "" and args.code or St.Code(name, fn)
        if not code then return refuse("the code of " .. name .. "'s " .. fn .. " is not kept yet: name its file once", St.Hint(name, fn)) end
        if args.code then St.Keep(name, fn, code) end
        local got
        AI.Carry({ act = "run", code = fn == "arm" and St.Chunk(code, e.options) or code, ui = e.side == "ui" }, function(a) got = a end)
        return GK.Observe({ ok = true, waiting = "the scene" }, function(_, final)
            if got == nil and not final then return nil end
            got = got or { ok = false, error = "the scene's code did not run" }
            if got.ok == false then return encoded(answer(false, "", name .. " (" .. e.side .. "): " .. tostring(got.error), "tell the owner")) end
            St.Mark(name, fn == "arm" and "" or false)
            local out = answer(true, name .. (fn == "arm" and " armed" or " disarmed") .. " (" .. e.side .. ")")
            out.now = nowBlock(false)
            return encoded(out)
        end, 8000)
    end
    local scenes = AI.SceneTable()
    local s = type(scenes) == "table" and scenes[name] or nil
    if type(s) ~= "table" then return refuse("no scene of the client is called " .. tostring(name), 'ai_scene {} lists them') end
    if fn == nil or fn == "" then
        if blind then return refuse("not knowable to a player: the scene's state", "name a call") end
        local out = answer(true, "read the scene " .. name)
        out.now = { scenes = { name .. ": " .. AI.SceneLine(s) .. " | calls: " .. table.concat(AI.SceneCalls(s), ", ") } }
        return encoded(out)
    end
    if type(s[fn]) ~= "function" then return refuse("the scene " .. name .. " has no call " .. tostring(fn), "call is one of: " .. table.concat(AI.SceneCalls(s), ", ")) end
    -- in the UI's context, as a window verb of the scene's would be
    local got
    AI.Carry({ act = "call", scene = name, call = fn }, function(a) got = a end)
    return GK.Observe({ ok = true, waiting = "the scene" }, function(_, final)
        if got == nil and not final then return nil end
        got = got or { ok = false, error = "the call did not end" }
        if got.ok == false then return refuse(tostring(got.error), "read the scene's state: ai_scene {\"name\":\"" .. name .. "\"}") end
        local out = answer(true, name .. "." .. fn .. "()" .. (got.answered and (" answered " .. got.answered) or ""))
        out.now = nowBlock(false)
        return encoded(out)
    end, 30000)
end

local LOCAL = { step = step, look = look, session = session, scene = scene }

--- One of the four tools - "session", "step", "look", "scene" - as the text a tool answers:
--- passed to the server while a level runs, answered here otherwise. Observed when it waits.
function AI.Tool(name, args)
    args = type(args) == "table" and args or {}
    -- a level that runs has a server even when its hello is late (a long step): pass the call on
    if AI.ServerUp() or (AI.ServerSeen ~= nil and gameState() == "Running") then return forward(name, args) end
    local fn = LOCAL[name]
    if not fn then return encoded(answer(false, "", "no such tool: " .. tostring(name), "the tools are ai_session, ai_step, ai_look, ai_scene")) end
    local ok, value = pcall(fn, args)
    if not ok then return encoded(answer(false, "", "the bridge failed: " .. tostring(value), "tell the owner")) end
    return value
end
