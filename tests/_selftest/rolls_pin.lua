-- GK.Rolls.Pin decides a roll in advance, and the event log carries what was rolled. Proved at
-- speed 1 and at the working speed, with the same verdicts:
--   - a passive skill check and a passive saving throw (two unlike roll kinds), of the host and
--     of a staged creature (two unlike creatures): pinned at 19 the hardest difficulty of the
--     game's data fails and at 20 it passes as a critical success; pinned at 2 difficulty 0
--     passes and at 1 it fails as a critical failure;
--   - `once` pins exactly one roll when the next is asked for after the pin has left (its
--     roll_unpin row), and pins a roll asked for before that: the gap the contract states;
--   - an active roll's window opens and closes as rows that carry the difficulty and the pinned
--     die, and the server's game_roll row says the same;
--   - Karmic Dice switched on and off inside the scope reads back from the roll manager, and does
--     not override a pin;
--   - after GK.Stage.Clean, GK.Rolls.Pins() is empty and GK.Stage.Verify is clean; a raw
--     Osi.AddBoosts left behind makes Verify name the boost.
-- How the scene is made: cheat (the stage scope, a spawned creature, a staged talker, the pins,
-- Karmic Dice, the talk: GK.Cheat.Talk, as no player's click on a character is built). The
-- dialogue's answer is the player's own path (fair); the rolls of the passive checks are asked of
-- the game by script (cheat). Everything read is observation.
-- Run: gatekit --instance <copy> test tests -k rolls_pin

test { level = "GustavX:Basic_Level_A",
           about = "A pinned die decides passive and active rolls of any creature and kind, once means one roll, and Clean leaves nothing behind." }

local EVENT = "GKRollsPin"

--- The game_roll rows of `who` and `kind` after `mark`, waiting until there are `count`.
local function rolls(mark, who, kind, count, seconds)
    local deadline = game.now() + (seconds or 20)
    local found
    repeat
        found = {}
        for _, row in ipairs(game.events(mark, { "game_roll" }).rows or {}) do
            local p = row.payload or {}
            if p.who == who and p.kind == kind then found[#found + 1] = row end
        end
        if #found >= count then return found end
        game.sleep(0.1)
    until game.now() > deadline
    check(false, ("%d %s roll(s) of %s, got %d"):format(count, kind, who, #found), found)
end

local function one(mark, who, kind) return rolls(mark, who, kind, 1)[1].payload end

--- Asks the game for a passive roll of `who` against the difficulty class `dc` (a uuid).
local function passive(who, kind, stat, dc)
    game.server(("Osi.RequestPassiveRoll(%q, %q, %q, %q, %q, 0, %q) return true"):format(who, who, kind, stat, dc, EVENT))
end

local function pin(spec)
    local code = ("return GK.Rolls.Pin{ who = %q, kind = %q, n = %d, once = %s }"):format(spec.who, spec.kind, spec.n, tostring(spec.once == true))
    local answer = game.server(code)
    check(answer.ok and answer.inForce, "the pin is in force", answer)
    return answer
end

local function unpin(who, kind)
    local answer = game.server(("return GK.Rolls.Unpin{ who = %q, kind = %q }"):format(who, kind))
    check(answer.ok, "the pin came off", answer)
end

-- One pinned roll against one difficulty: the verdict the pin decides.
local function decided(case, n, dc, wanted)
    pin{ who = case.who, kind = case.kind, n = n }
    local mark = game.mark()
    passive(case.who, case.kind, case.stat, dc.id)
    local r = one(mark, case.who, case.kind)
    unpin(case.who, case.kind)
    check(r.natural == n, case.label .. ": the natural die is the pin's", n, r)
    check(r.pinned == n, case.label .. ": the row says the pin was in force", r)
    check(r.success == wanted.success, ("%s: pinned at %d against %s (%d)"):format(case.label, n, dc.name, dc.value), r)
    check(r.critical == wanted.critical, case.label .. ": critical", wanted.critical, r)
    check(r.dc == dc.value, case.label .. ": the row carries the difficulty", dc, r)
    return r
end

local function cases_of(host, staged)
    return {
        { label = "host Perception", who = host, kind = "SkillCheck", stat = "Perception" },
        { label = "staged Constitution save", who = staged, kind = "SavingThrow", stat = "Constitution" },
        { label = "host Dexterity save", who = host, kind = "SavingThrow", stat = "Dexterity" },
        { label = "staged Perception", who = staged, kind = "SkillCheck", stat = "Perception" },
    }
end

local function passive_verdicts(cases, hardest, zero)
    local line = {}
    for _, case in ipairs(cases) do
        local r19 = decided(case, 19, hardest, { success = false })
        check(r19.total < hardest.value, case.label .. ": a 19 cannot pass the hardest difficulty", r19)
        decided(case, 20, hardest, { success = true, critical = "success" })
        decided(case, 2, zero, { success = true })
        decided(case, 1, zero, { success = false, critical = "failure" })
        line[#line + 1] = case.label .. " (bonus " .. tostring(r19.bonus) .. ")"
    end
    return line
end

-- once: one roll asked after the pin has left is free; one asked before it left is still pinned.
local function once(case, dc)
    local pinned = pin{ who = case.who, kind = case.kind, n = 20, once = true }
    local mark = game.mark()
    passive(case.who, case.kind, case.stat, dc.id)
    local first = one(mark, case.who, case.kind)
    check(first.natural == 20 and first.pinned == 20, case.label .. ": the once-pin decided the first roll", first)
    -- the pin has left once its roll_unpin row is written
    local gone
    for _ = 1, 100 do
        for _, row in ipairs(game.events(mark, { "roll_unpin" }).rows or {}) do
            if row.payload.track == pinned.track then gone = row end
        end
        if gone then break end
        game.sleep(0.1)
    end
    check(gone and gone.payload.why == "once" and not gone.payload.stuck, case.label .. ": the once-pin left by itself", gone)
    check(#game.server("return GK.Rolls.Pins()") == 0, case.label .. ": nothing pinned once it has left")
    local after = game.mark()
    for _ = 1, 6 do passive(case.who, case.kind, case.stat, dc.id) end
    local later = rolls(after, case.who, case.kind, 6)
    local twenties = 0
    for _, row in ipairs(later) do
        check(row.payload.pinned == nil, case.label .. ": a roll after the pin left is not pinned", row.payload)
        if row.payload.natural == 20 then twenties = twenties + 1 end
    end
    check(twenties < 6, case.label .. ": six free rolls are not all 20", later)
    -- inside the gap: two rolls asked together, the pin goes after the first finishes - both pinned
    pin{ who = case.who, kind = case.kind, n = 20, once = true }
    local both = game.mark()
    passive(case.who, case.kind, case.stat, dc.id)
    passive(case.who, case.kind, case.stat, dc.id)
    local pair = rolls(both, case.who, case.kind, 2)
    check(pair[1].payload.natural == 20 and pair[2].payload.natural == 20, case.label .. ": a roll asked before the pin left is pinned too", pair)
    return { removal = gone.payload.ticks }
end

-- The active roll of a staged talker's dialogue: the window's rows and the server's row.
local function active(host)
    game.server("GK.FixtureT = GK.Fixture.Talker{ dialog = 'roll' } return true")
    game.wait("server", "GK.Fixture.Ready('Talker')", 15)
    pin{ who = host, kind = "SkillCheck", n = 20 }
    local mark = game.mark()
    game.server("return GK.Cheat.Talk(nil, GK.FixtureT.entities[1].id)")
    game.wait_attention({ "dialogue", "dialogue_choice" }, 30, mark)
    game.wait("ui", "GK.Gui.DialoguePhase() and GK.Gui.DialoguePhase().phase == 'choice'", 30, 0.2)
    game.ui("return GK.Answer(1)")
    game.wait_attention({ "roll" }, 30, mark)
    for _ = 1, 40 do
        if not game.ui("return GK.Gui.Blocking().window") then break end
        game.ui("return GK.Gui.Resolve({ roll = 'throw', dialogue = 'last' })")
        game.sleep(0.4)
    end
    check(not game.ui("return GK.Gui.Blocking().window"), "the dialogue and the roll closed", game.ui("return GK.Gui.Blocking()"))
    game.sleep(0.5)
    local rows = game.events(mark, { "roll", "roll_ended", "game_roll" }).rows
    local opened, ended, server
    for _, row in ipairs(rows) do
        if row.kind == "roll" then opened = row.payload
        elseif row.kind == "roll_ended" then ended = row.payload
        elseif row.kind == "game_roll" and row.payload.active then server = row.payload end
    end
    check(opened and opened.dc and opened.dc > 0, "the window's opening row carries the difficulty", opened)
    check(opened.who == host, "the opening row names the roller", opened)
    check(opened.modifiers and #opened.modifiers > 0, "the opening row lists the modifiers", opened)
    check(ended and ended.natural == 20 and ended.success == true and ended.critical == "success", "the closing row carries the pinned die", ended)
    check(ended.dc == opened.dc and ended.total == 20 + (ended.bonus or 0), "the closing row's total is the die and the bonus", ended)
    check(server and server.natural == 20 and server.pinned == 20 and server.dc == opened.dc and server.kind == "SkillCheck",
        "the server's game_roll row of the active roll says the same", server)
    unpin(host, "SkillCheck")
    return opened
end

local function karmic(host, zero)
    local before = game.server("return GK.Rolls.Karmic()")
    local on = game.server("return GK.Rolls.SetKarmic(true)")
    check(on.ok and on.mode == "WeightedRandom", "Karmic Dice on: the roll manager follows", on)
    pin{ who = host, kind = "SkillCheck", n = 1 }
    local mark = game.mark()
    for _ = 1, 3 do passive(host, "SkillCheck", "Perception", zero.id) end
    for _, row in ipairs(rolls(mark, host, "SkillCheck", 3)) do
        check(row.payload.natural == 1, "Karmic Dice does not override a pin", row.payload)
    end
    unpin(host, "SkillCheck")
    local off = game.server("return GK.Rolls.SetKarmic(false)")
    check(off.ok and off.mode == "Random", "Karmic Dice off: the roll manager follows", off)
    return before
end

-- After Clean: nothing pinned, Verify clean (asked again while only a moment's matter remains).
local function clean()
    game.server("return GK.Stage.Clean()")
    local pins, verdict
    for _ = 1, 30 do
        pins = game.server("return GK.Rolls.Pins()")
        verdict = game.server("return GK.Stage.Verify()")
        if #pins == 0 and verdict.clean then break end
        game.sleep(0.2)
    end
    check(#pins == 0, "Clean took every pin off", pins)
    check(verdict.clean, "Verify is clean after Clean", verdict)
end

local function raw_boost(host)
    game.server(("Osi.AddBoosts(%q, 'Initiative(5)', 'GKRawTest', %q) return true"):format(host, host))
    local verdict
    for _ = 1, 20 do
        verdict = game.server("return GK.Stage.Verify()")
        if not verdict.clean then break end
        game.sleep(0.1)
    end
    local named = false
    for _, line in ipairs(verdict.differences or {}) do
        if contains(line, "Initiative(5)") and contains(line, "not tracked") then named = true end
    end
    check(not verdict.clean and named, "Verify names a raw boost left behind", verdict)
    game.server(("Osi.RemoveBoosts(%q, 'Initiative(5)', 0, 'GKRawTest', %q) return true"):format(host, host))
    for _ = 1, 20 do
        verdict = game.server("return GK.Stage.Verify()")
        if verdict.clean then break end
        game.sleep(0.1)
    end
    check(verdict.clean, "Verify is clean once the raw boost is gone", verdict)
end

local function proof(speed)
    local set = game.server(("return GK.Speed.Set(%s)"):format(speed == "working" and "'working'" or tostring(speed)))
    local scale = set.engine
    local host = game.server("return Osi.GetHostCharacter()"):match("(%x+%-%x+%-%x+%-%x+%-%x+)$")
    -- the difficulties from the game's data: the hardest, and zero
    local dcs = game.server([[
        local hardest, zero
        for _, id in ipairs(Ext.StaticData.GetAll('DifficultyClass')) do
            local d = Ext.StaticData.Get(id, 'DifficultyClass')
            local v = d.Difficulties[1]
            if v and (not hardest or v > hardest.value) then hardest = { id = id, name = d.Name, value = v } end
            if v == 0 and not zero then zero = { id = id, name = d.Name, value = v } end
        end
        return { hardest = hardest, zero = zero }]])
    check(dcs.hardest and dcs.zero, "the game's data has a hardest difficulty and a zero one", dcs)
    local begun = game.server("return GK.Stage.Begin({ fresh = true })")
    check(begun.scope, "the scope is open", begun)
    -- refused outside the rules
    local bad = game.server("local ok, why = pcall(GK.Rolls.Pin, { kind = 'Perception', n = 20 }) return why")
    check(contains(bad, "SkillCheck") and contains(bad, "Initiative"), "an unknown kind lists the kinds", bad)
    -- a staged creature: a neutral guard next to the host (its own chunk: staging and cleaning
    -- are never in one)
    local staged = game.server(("return GK.Stage.Spawn{ template = GK.Fixture.Defaults.enemy, faction = 'neutral', near = %q, distance = 4 }[1]"):format(host))
    game.wait("server", ("GK.Stage.Ready({ %q }) and Ext.Entity.Get(%q).BoostsContainer ~= nil"):format(staged, staged), 15)
    game.sleep(1.0)
    local line = passive_verdicts(cases_of(host, staged), dcs.hardest, dcs.zero)
    local gapHost = once({ label = "host Perception", who = host, kind = "SkillCheck", stat = "Perception" }, dcs.zero)
    local gapStaged = once({ label = "staged Constitution save", who = staged, kind = "SavingThrow", stat = "Constitution" }, dcs.zero)
    -- Initiative takes the game's Initiative boost; the bonus shows on the creature, Clean takes it off
    local init = pin{ who = staged, kind = "Initiative", n = 100 }
    check(init.boost == "Initiative(100)", "Initiative is the game's Initiative boost", init)
    local opened = active(host)
    local before = karmic(host, dcs.zero)
    clean()
    raw_boost(host)
    print(("speed %s (engine %s): passive verdicts held for %s against %s (%d) and %s (0); once: the pin left %s ticks after "
        .. "its roll finished (host), %s (staged); active roll: %s, difficulty %s; Karmic Dice before: %s"):format(
        tostring(speed), tostring(scale), table.concat(line, ", "), dcs.hardest.name, dcs.hardest.value, dcs.zero.name,
        tostring(gapHost.removal), tostring(gapStaged.removal), tostring(opened.check), tostring(opened.dc), fmt(before)))
end

function run(game)
    game.wait_state("Running", "server", 120)
    proof(1)
    proof("working")
    game.server("GK.Speed.Set(1) return true")
end
