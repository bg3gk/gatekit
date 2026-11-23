-- GK.Fixture scenes in the empty test level, each proved through the attention record: a fight that
-- starts (a hostile that sees the party starts it), an opportunity-attack reaction (a creature
-- leaving a melee wielder's reach without Disengage provokes it; the reactor's reaction is set to
-- ask), a member downed at 0 hit points, a dialogue owned by its NPC and its skill check (d20 +
-- modifier against the DC), a trade, a loot, a message box. Then GK.Fixture.Clear() and the same again.

test { level = "GustavX:Basic_Level_A",
           -- each fixture opens its own blocking state, and the test reads them through GK.Attention in the game
           surprises = { "box", "combat_started", "dialogue", "loot", "reaction", "roll", "trade" },
           -- the interface opens a reaction prompt's window only for an interruptor whose player
           -- properties say it is the selected character (gui::VMPlayerCharacterProperties
           -- IsSelectedCharacter); in a game drawn from no frame those properties are made after the
           -- character was selected and never learn it, so the prompt freezes the fight unanswerable
           drawn_start = "a reaction prompt's window opens only for the selected character, which a game drawn from no frame never tells its interface" }

local function seq(side) return game.lua(side, "return GK.Attention.Seq") end

--- Waits until `side`'s record has `what` after `mark`; answers the event and how long after the
--- trigger (on that side's clock) it was recorded.
local function expect(side, mark, what, started, timeout)
    local expr = ("(function() for _, e in ipairs(GK.Attention.Since(%d).events) do if e.what == %q then return e end end end)()")
        :format(mark, what)
    local event = game.wait(side, expr, timeout or 30, 0.1)
    return event, started and (event.at - started) or nil
end

local function now(side) return game.lua(side, "return Ext.Utils.MonotonicTime()") end

local function step(side, what, trigger, timeout)
    local mark, started = seq(side), now(side)
    game.lua(trigger[1], trigger[2])
    local _, ms = expect(side, mark, what, started, timeout)
    return ms
end

local function resolveAll(policy)
    for _ = 1, 30 do
        local open = game.ui("return GK.Gui.Blocking().window")
        if not open then return end
        game.ui("return GK.Gui.Resolve(" .. policy .. ")")
        game.sleep(0.6)
    end
    check(false, "a window stayed open", game.ui("return GK.Gui.Blocking()"))
end

local function round(n)
    local delays = {}
    local before = game.server("return #Ext.Entity.GetAllEntitiesWithComponent('ServerCharacter')")

    delays.combat_started = step("server", "combat_started", { "server", "GK.FixtureF = GK.Fixture.Fight{} return true" })
    delays.combat_ended = step("server", "combat_ended",
        { "server", "Osi.Die(GK.FixtureF.entities[1].id, 0, 'NULL_00000000-0000-0000-0000-000000000000', 0, 0) return true" })
    game.server("return GK.Fixture.Clear()")

    game.server("GK.FixtureF = GK.Fixture.Fight{provoke = true} return true")
    local ready = game.wait("server", "(function() local r, why = GK.Fixture.ProvokeReady() if r == nil then return nil end return { r, why } end)()", 20, 0.3)
    check(ready[1] == true, "the provoke was not armed", ready)
    delays.reaction = step("client", "reaction", { "server", "return GK.Act.EndTurn(nil)" }, 40)
    delays.reaction_ended = step("client", "reaction_ended", { "ui", "return GK.Gui.Resolve({ reaction = 'react' })" })
    game.sleep(1.0)
    delays.combat_ended_by_clear = step("server", "combat_ended", { "server", "return GK.Fixture.Clear()" })

    delays.downed = step("server", "downed", { "server", "return GK.Fixture.Down{}" })
    game.wait("server", "Osi.GetHitpoints(Osi.GetHostCharacter()) > 0", 10)

    game.server("GK.FixtureT = GK.Fixture.Talker{ dialog = 'roll' } return true")
    delays.dialogue = step("client", "dialogue", { "server", "return GK.Cheat.Talk(nil, GK.FixtureT.entities[1].id)" })
    delays.roll = step("client", "roll", { "ui", "return GK.Answer(1)" })
    resolveAll("{ roll = 'throw', dialogue = 'last' }")

    game.server("GK.FixtureTr = GK.Fixture.Trader{} return true")
    game.sleep(1.0)
    delays.trade = step("client", "trade", { "server", "return GK.Cheat.Trade(nil, GK.FixtureTr.entities[1].id)" })
    local goods = game.wait("ui", "(function() local s = GK.Gui.Blocking().summary return s and #s.trader.items > 0 and s end)()", 10)
    check((goods.traderGold or 0) > 0 and (goods.playerGold or 0) > 0, "gold on both sides", goods)
    game.ui("return GK.Gui.Close('Trade')")

    game.server("GK.FixtureL = GK.Fixture.Loot{} return true")
    game.wait("server", "GK.Fixture.Ready('Loot')", 10)
    delays.loot = step("client", "loot", { "server", "return GK.Cheat.Use(nil, GK.FixtureL.entities[1].id)" })
    game.ui("return GK.Gui.Close('ContainerWidget')")

    delays.box = step("client", "box", { "server", "return GK.Fixture.Box('GateKit')" })
    game.ui("return GK.Gui.Resolve({ box = 'affirmative' })")

    local cleared = game.server("return GK.Fixture.Clear()")
    check(#cleared.failed == 0, "some undo failed", cleared)
    game.wait("server", ("#Ext.Entity.GetAllEntitiesWithComponent('ServerCharacter') == %d"):format(before), 15)
    local after = game.server("local h = Osi.GetHostCharacter() return { inCombat = Osi.IsInCombat(h), made = #GK.Fixture.Made(), immortal = Osi.IsImmortal(h) }")
    eq(after.inCombat, 0, "no fight is left")
    eq(after.made, 0, "nothing of the fixtures is left")
    eq(after.immortal, 0, "the member is mortal again")
    check(game.ui("return GK.Gui.Blocking().window") == nil, "no window is left")
    local line = {}
    for _, k in ipairs(sorted(keys(delays))) do line[#line + 1] = k .. " " .. math.floor(delays[k] or -1) .. " ms" end
    print(("round %d: %s"):format(n, table.concat(line, ", ")))
end

function run(game)
    game.wait_state("Running", "server", 120)
    round(1)
    round(2)
end
