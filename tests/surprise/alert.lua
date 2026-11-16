-- The feat Alert keeps the status off its holder and off nobody else: the passive's boost is
-- StatusImmunity(SURPRISED) (the game's data, `gatekit data stat Alert`; bg3.wiki, "Alert": "can't
-- be Surprised"), and the game gives the status through its ordinary status request, which an
-- immunity refuses (code:unplaced::StatusRequestSystemGatherCombatEvents; research/studies/surprised.md).
-- The hit from hiding of test:surprise/from_hiding lands on a target that has Alert: the target
-- joins the fight unsurprised, its ally beside it is surprised as before.
-- Staged (cheat): the scene of tests/surprise/_surprise.lua with the host hidden; the passive Alert on the
-- target (a raw Osi.AddPassive on a staged creature, which the clean deletes); the attack die
-- pinned to hit; the attack ordered by script. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k surprise/alert --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A target with Alert is not surprised by a hit from hiding; its ally in the fight is." }

local S = require("_surprise")

function run(game)
    S.watch(game)
    local cast = step("the host hidden, two hostile humans turned away 8 m off, the target with Alert", "cheat", function()
        local c = S.scene(game, "hidden", 8)
        local pinned = game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'Attack', n = 15 }", c.host))
        check(pinned.ok and pinned.inForce, "the attack die is pinned", pinned)
        game.server(string.format("Osi.AddPassive(%q, 'Alert') return true", c.target))
        game.wait("server", string.format("Osi.HasPassive(%q, 'Alert') == 1", c.target), 10)
        return c
    end)
    step("the scene is as staged", "observation", function()
        S.quiet(game, cast)
        need(game.server(string.format("return Osi.HasPassive(%q, 'Alert')", cast.ally)) == 0, "the ally has no Alert")
    end)
    local mark = game.mark()
    step("the hidden host shoots the target", "cheat", function() S.attack(game, cast, cast.target, "ranged") end)
    step("the ally is surprised, the target with Alert is not", "observation", function()
        row(mark, "game_hit", function(p) return S.uuid(p.who) == cast.target and S.uuid(p.by) == cast.host end, 60)
        S.joined(mark, cast.target)
        S.surprised(mark, cast.ally)
        S.never_surprised(game, mark, cast.target, "the target with Alert")
    end)
end
