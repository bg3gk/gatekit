-- AI.Mode and AI.Session (server side): how much the agent may know, decided in one place, and
-- what a session was started with.
--
--   AI.Mode.name            "open" or "blind"
--   AI.Mode.Scope(memory)   the scope GK.See's readers are asked with
--   AI.Mode.Row(row)        whether a row of the event log may be told
--   AI.Mode.Part(key)       whether a part of `now` or of a look is told
--   AI.Mode.Knows(id)       whether a thing may be named or acted on
--   AI.Mode.Refuse(what)    the sentence of a refusal: "not knowable to a player: ..."
--   AI.Session              { mode, prompt, promptFrom, plan }: kept in a file of the copy
--   AI.Session.Start(args)  a session begun: the mode, the owner's prompt, a plan
--
-- blind: the agent knows what a player at the screen could know and nothing else. GateKit classes
-- what it reads the same way - a reader's scope (`seen`: perceived now; `known`: also remembered;
-- `all`: omniscient) and a row's `knows` (`player` or `omniscient`) - and every part of the bridge
-- asks this file which of them it may use:
--   creatures and things   a step's block lists what is perceived now (`seen`) in both modes.
--                          blind: a target may also be one seen earlier (`known`), and nothing
--                          else. open: a look lists everything within the radius (`all`), the
--                          unperceived marked `unseen`, and a target may be anything on the level
--   rows of the log        blind: `knows = player` only. open: all
--   rolls                  blind: only the roll window, in `waiting`. open: every roll thrown
--   scenes, route, plan    blind: not told (a scene's state is the director's; a route with
--                          conditions read from the game is knowledge a player has not). open: told
--   the agent's own notes, the owner's prompt, lines heard, the party   both

AI.Mode = AI.Mode or { name = "open" }
local M = AI.Mode
local GK = AI.GK
local U = AI.Util

M.Names = { open = "everything the bridge can read", blind = "only what a player at the screen could know" }
local DIRECTED = { scenes = true, route = true, plan = true, rolls = true }

function M.Blind() return M.name == "blind" end

--- The sentence a refusal of the blind mode begins with.
function M.Refuse(what) return "not knowable to a player: " .. tostring(what) end

--- The scope a reader of GK.See is asked with: blind "seen" (with `memory`: "known"), open "all".
function M.Scope(memory)
    if M.Blind() then return memory and "known" or "seen" end
    return "all"
end

--- Whether a row of the event log may be told: in blind mode only one a player could know.
function M.Row(row)
    return not M.Blind() or row.knows == "player"
end

--- Whether a part of `now` (or of a look) is told in this mode.
function M.Part(key)
    return not (M.Blind() and DIRECTED[key])
end

--- Whether a thing (a uuid) may be named to the agent or acted on by it: always in open mode; in
--- blind mode a party member, what the party perceives now, or what it saw earlier on this level.
function M.Knows(id)
    if not M.Blind() then return true end
    local uuid = U.uuidOf(id) or id
    for _, m in ipairs(U.members()) do
        if (U.uuidOf(m) or m) == uuid then return true end
    end
    if U.try(GK.See.PartyPerceives, uuid) then return true end
    return U.try(GK.See.Remembers, uuid) and true or false
end

-- ---------------------------------------------------------------------------------------
-- The session

AI.Session = AI.Session or {}
local S = AI.Session
local FILE = "Gwynerva_GateKitAI/session.json"
S.MaxPrompt = 16000

local function save()
    pcall(Ext.IO.SaveFile, FILE, Ext.Json.Stringify({ mode = M.name, prompt = S.prompt, promptFrom = S.promptFrom }))
end

if not S.Restored then
    S.Restored = true
    local ok, text = pcall(Ext.IO.LoadFile, FILE)
    local kept = ok and text and text ~= "" and select(2, pcall(Ext.Json.Parse, text)) or nil
    if type(kept) == "table" then
        if M.Names[kept.mode] then M.name = kept.mode end
        S.prompt, S.promptFrom = kept.prompt, kept.promptFrom
    end
end

--- Begins a session: `args.mode` ("open" or "blind"; left out: as it was), `args.prompt` (the
--- owner's own text for the model - who the character is, what is wanted, how to talk; nothing of
--- it is read by the bridge; "" forgets it), `args.prompt_from` (where the host read it from),
--- `args.plan` (open mode only: a plan of the mod by name, or "off"), `args.step` (the plan's step
--- to stand on). The next `now` of every cursor is the whole picture.
--- Mode and prompt are kept in a file of the copy and outlive a load. { ok, error?, hint? }.
function S.Start(args)
    args = type(args) == "table" and args or {}
    if args.mode ~= nil and args.mode ~= "" then
        if not M.Names[args.mode] then return { ok = false, error = "no such mode: " .. tostring(args.mode), hint = "mode is open or blind" } end
        M.name = args.mode
    end
    if args.prompt ~= nil then
        local text = tostring(args.prompt)
        if #text > S.MaxPrompt then
            return { ok = false, error = "the prompt is " .. #text .. " bytes; the most is " .. S.MaxPrompt, hint = "shorten it" }
        end
        S.prompt = text ~= "" and text or nil
        S.promptFrom = S.prompt and args.prompt_from or nil
    end
    local wantsPlan = args.plan ~= nil and args.plan ~= ""
    if M.Blind() then
        if AI.Plan.Current then AI.Plan.Off() end
        if wantsPlan and args.plan ~= "off" then
            save()
            return { ok = false, error = M.Refuse("a route plan with conditions read from the game"),
                     hint = "in blind mode the goals are words of the prompt; a plan belongs to mode open" }
        end
    elseif wantsPlan then
        local loaded = args.plan == "off" and AI.Plan.Off() or AI.Plan.Load(args.plan, args.step)
        if not loaded.ok then save(); return { ok = false, error = loaded.error, hint = "plan is the name of a plan of the mod, or off" } end
    elseif args.step ~= nil then
        local moved = AI.Plan.Go(tonumber(args.step))
        if not moved.ok then return { ok = false, error = moved.error, hint = "step is a number of ai_look {\"part\":\"plan\"}" } end
    end
    save()
    -- the next block is the whole picture; what was said since the cursor is still said once
    for _, session in pairs(AI.Sessions or {}) do
        session.first, session.near, session.scenes, session.notes = true, {}, {}, nil
    end
    AI.Row("ai_session", { mode = M.name, prompt = S.prompt and #S.prompt or nil, promptFrom = S.promptFrom,
        plan = AI.Plan.Current and AI.Plan.Current.name or nil })
    return { ok = true }
end
