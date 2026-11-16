-- GK.Camera (server side): the camera's focus asked from the server.
--
-- The camera is the client's (Client/Camera.lua does the move and observes it). The server knows
-- where every object is; the client does not: a creature far from the camera is a stub there whose
-- position is stale. So the server's GK.Camera.Focus sends the client the server's own point of
-- each uuid, on GK_Camera, and the client focuses the entity when it holds it whole and the point
-- when it holds a stub. The client's observed answer comes back on the same channel; the server's
-- answer is that answer, with the server ticks it waited.
--
-- A far creature is asleep and not drawn (research/studies/camera-focus.md). With options.wake the
-- focus first wakes it - "one": that creature alone (GK.Wake.One), "place": the space around it as
-- a party member standing there would (GK.Wake.Place) - waits until the client holds it whole, and
-- then moves the camera, so that the game's own fade to black comes and the creature is there when
-- it lifts. What a focus woke is held in Camera.Held until GK.Camera.Return, a newer focus or the
-- stage's clean releases it.

local Camera = GK.Camera or {}
GK.Camera = Camera

Camera.Answers = Camera.Answers or {}
Camera.Unasked = Camera.Unasked or {}
Camera.NextId = Camera.NextId or 0
Camera.Moves = Camera.Moves or 0
Camera.AnswerMs = 8000         -- a guard, in ms of a running game: the client settles well before
Camera.FarAnswerMs = 20000     -- the same for a move past the snap distance: the black waits for the place
Camera.WakeMs = 60000          -- a guard over a focus that wakes first: each part has its own limit

Camera.Channel = Camera.Channel or Ext.Net.CreateChannel(ModuleUUID, "GK_Camera", function(msg)
    if type(msg) ~= "table" or not msg.id then return end
    if GK.Camera.Unasked[msg.id] then GK.Camera.Unasked[msg.id] = nil return end
    GK.Camera.Answers[msg.id] = msg.answer or { ok = false }
end)

local function pointOf(t)
    if type(t) ~= "table" then return nil end
    local x, y, z = t.x or t[1], t.y or t[2], t.z or t[3]
    if type(x) == "number" and type(y) == "number" and type(z) == "number" then return { x, y, z } end
end

local function ask(message, sent, within)
    Camera.NextId = Camera.NextId + 1
    local id = Camera.NextId
    message.id = id
    local ok, problem = pcall(function() Camera.Channel:Broadcast(message) end)
    if not ok then return { ok = false, error = "could not reach the client: " .. tostring(problem) } end
    sent.id = id
    return GK.Observe(sent, function(ticks, final)
        local answer = Camera.Answers[id]
        if answer then
            Camera.Answers[id] = nil
            answer.serverTicks = ticks
            return answer
        end
        if final then
            Camera.Unasked[id] = true
            return { ok = false, error = "the client did not answer in " .. ticks .. " server ticks" }
        end
    end, within or Camera.AnswerMs)
end
-- the same question for the other server files that need the client's word (Server/Wake.lua)
Camera.AskClient = ask

-- A message nobody waits on (the stage's clean).
local function tell(message)
    Camera.NextId = Camera.NextId + 1
    message.id = Camera.NextId
    Camera.Unasked[message.id] = true
    return pcall(function() Camera.Channel:Broadcast(message) end)
end

-- Releases what a focus woke. Answers GK.Wake.Release's answer (observed), or nil.
local function release(held)
    if not (held and held.handle and GK.Wake) then return nil end
    return GK.Wake.Release(held.handle)
end

-- Follows an observed value to its end from the stage's queue, for a call nobody else follows.
local function drive(value)
    local follow = GK.Follow(value)
    local function look()
        if follow() == nil then GK.Stage.AfterTicks(1, look) end
    end
    look()
end

-- The stage's clean puts the camera's switches back and the camera on its character: once a scope.
local function trackHold()
    if not (GK.Stage and GK.Stage.Track) then return end
    pcall(GK.Stage.Track, { kind = "other", key = "camera:hold", note = "the camera's far focus: the leash raised, the camera away",
        undo = function()
            Camera.Moves = Camera.Moves + 1
            Camera.Held = nil
            tell({ op = "return", options = { glide = true } })
            tell({ op = "leash" })
            return true
        end })
end

--- GK.Camera.Focus from the server: `at` (an entity's uuid, a point {x, y, z}, or a list of two)
--- is sent to the client with the server's point of every uuid, so that a far creature (a stub on
--- the client) is focused where the server has it. Options as the client's, and options.wake:
--- "one" wakes the creature alone (GK.Wake.One), "place" the space around the creature or the point
--- (GK.Wake.Place), before the camera moves; the wake is a cheat, so a fair step is refused it.
--- Observed: the client's answer once the camera settled - with `drawn`, `far`, and after a far
--- move `faded` and `blackTicks` - with `serverTicks`; with a wake also `wake`, `woke` (the wake's
--- own answer) and `wakeTicks`. What it woke stays awake until GK.Camera.Return, a newer focus or
--- GK.Stage.Clean. A wait that ran out says which: the wake, the client's "whole", or the fade.
function Camera.Focus(at, options)
    options = options or {}
    local list = at
    if type(at) ~= "table" or pointOf(at) then list = { at } end
    local points, kinds = {}, {}
    for _, item in ipairs(list) do
        if type(item) == "string" then
            local ok, x, y, z = pcall(Osi.GetPosition, item)
            if not (ok and x) then return { ok = false, error = "no such entity: " .. item } end
            points[item] = { x, y, z }
            local okc, character = pcall(Osi.IsCharacter, item)
            kinds[item] = (okc and character == 1) and "character" or "item"
        elseif not pointOf(item) then
            return { ok = false, error = "not a uuid or a point: " .. tostring(item) }
        end
    end
    local wake = options.wake
    if wake ~= nil then
        if wake ~= "one" and wake ~= "place" then
            return { ok = false, error = "options.wake is \"one\" or \"place\", not " .. tostring(wake) }
        end
        if GK.Verbs and GK.Verbs.Scope == "fair" then
            return { ok = false, error = "options.wake is a cheat (GK.Wake keeps a creature or a place awake) and this is a fair step: "
                .. "wake in a cheat step (GK.Wake.One, GK.Wake.Place) and focus here without it, or call the whole focus in a cheat step" }
        end
        if #list ~= 1 then return { ok = false, error = "options.wake takes one thing to focus, not " .. #list } end
        if wake == "one" and kinds[list[1]] ~= "character" then
            return { ok = false, error = "wake = \"one\" keeps a creature awake: give a character's uuid, or wake = \"place\" for a point or an item" }
        end
        if not GK.Wake then return { ok = false, error = "GK.Wake is not loaded" } end
    end
    Camera.Moves = Camera.Moves + 1
    local move = Camera.Moves
    local target = type(list[1]) == "string" and list[1] or nil

    -- a newer focus cancels an older hold, unless it asks for the same wake of the same thing
    local old = Camera.Held
    local same = old ~= nil and wake ~= nil and old.wake == wake and old.target ~= nil and old.target == target
    if old and not same then
        Camera.Held = nil
        release(old)
    end

    local toClient = {}
    for k, v in pairs(options) do
        if k ~= "wake" and not (wake and k == "hold") then toClient[k] = v end
    end
    local function send()
        trackHold()
        return ask({ op = "focus", at = at, points = points, kinds = kinds, options = toClient },
            { ok = true, sent = "focus" }, Camera.FarAnswerMs)
    end
    if not wake then return send() end

    local stage, follow, woke, wakeTicks = "wake", nil, nil, nil
    if same then
        stage, woke, wakeTicks = "focus", old.woke, 0
        follow = GK.Follow(send())
    else
        local first = wake == "one" and GK.Wake.One(list[1]) or GK.Wake.Place(list[1])
        follow = GK.Follow(first)
    end
    return GK.Observe({ ok = true, sent = "focus", wake = wake }, function(ticks, final)
        if Camera.Moves ~= move then
            return { ok = false, error = "a newer camera move took over", ticks = ticks }
        end
        local answer = follow()
        if answer == nil then
            if final then return { ok = false, stage = stage, error = "the " .. stage .. " did not answer in " .. ticks .. " server ticks" } end
            return nil
        end
        if stage == "wake" then
            woke, wakeTicks = answer, ticks
            if not answer.ok then
                -- nothing is left awake by a focus that did not happen
                local handle = answer.helper or (wake == "one" and list[1]) or nil
                if handle then GK.Wake.Release(handle) end
                return { ok = false, stage = "wake", wake = wake, woke = answer, serverTicks = ticks,
                    error = "the wake did not finish: " .. tostring(answer.error) }
            end
            Camera.Held = { wake = wake, target = target, handle = answer.helper or list[1], woke = answer, move = move }
            stage, follow = "focus", GK.Follow(send())
            return nil
        end
        answer.wake, answer.woke, answer.wakeTicks, answer.serverTicks = wake, woke, wakeTicks, ticks
        if answer.ok and tonumber(options.hold) and options.hold > 0 then
            GK.Stage.AfterMs(math.floor(options.hold * 1000), function()
                if Camera.Moves == move then drive(GK.Camera.Return()) end
            end)
        end
        return answer
    end, Camera.WakeMs)
end

--- GK.Camera.Return from the server: the client's restore to the controlled character - from past
--- the snap distance the game's own fade, awaited - and then the release of what the focus woke.
--- Observed as the client's (with `leashRestored`), with `released`: GK.Wake.Release's answer -
--- who slept again - when the focus had woken something.
function Camera.Return(options)
    Camera.Moves = Camera.Moves + 1
    local move = Camera.Moves
    local held = Camera.Held
    Camera.Held = nil
    local back = ask({ op = "return", options = options }, { ok = true, sent = "return" }, Camera.FarAnswerMs)
    if not held then return back end
    local stage, follow, answer = "return", GK.Follow(back), nil
    return GK.Observe({ ok = true, sent = "return", releasing = held.handle }, function(ticks, final)
        local now = follow()
        if now == nil then
            if final then return { ok = false, stage = stage, error = "the " .. stage .. " did not answer in " .. ticks .. " server ticks" } end
            return nil
        end
        if stage == "return" then
            answer = now
            -- the release comes after the camera is back, so the place is there until the black
            local released = Camera.Moves == move and release(held) or nil
            if released == nil then
                answer.serverTicks = ticks
                return answer
            end
            stage, follow = "release", GK.Follow(released)
            return nil
        end
        answer.released, answer.serverTicks = now, ticks
        if not now.ok then
            answer.ok = false
            answer.error = "the camera is back, the release did not finish: " .. tostring(now.error)
        end
        return answer
    end, Camera.WakeMs)
end

return Camera
