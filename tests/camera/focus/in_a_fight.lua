-- In a fight, on the player's own turn, GK.Camera.Focus moves the camera and GK.Camera.Return
-- brings it back by the game's restore (docs/contracts/lua-api.md, section 22). On the player's
-- turn the camera is in CameraMode 9; the combat target written with TargetMode 4 (as the game
-- writes it on a ranged attack) did not move it there, so the focus goes through the camera
-- behaviour's targets as outside a fight, and says `fight = true`; the return is the combat
-- target's restore (TargetMode 1, the Position reset, which otherwise overrides the targets and
-- the return silently fails). Stands on test:camera/focus/matrix.
-- Staging (cheat): the host unkillable and first in initiative, a hostile goblin 8 m to the screen's
-- right, the fight. Acting (fair): the focus on the goblin on the host's turn, the return.
-- Reading (observation): the answers, the turn row, the positions.
-- Run: gatekit --instance <copy> test tests -k camera/focus/in_a_fight --speed working

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
       about = "In a fight, on the host's turn, the focus moves the camera onto a goblin and the game's restore brings it back to the host." }

local F = require("_focus")

function run(game)
    game.wait_state("Running", "server", 120)
    local host = F.uuid(game.server("return Osi.GetHostCharacter()"))
    local staged = game.mark()
    local goblin = step("the host first, a hostile goblin 8 m off, the fight", "cheat", function()
        F.home(game)
        local _, p = F.place(game, 8)
        local g = F.uuid(game.server(string.format([[
            GK.Stage.Immortal(true, Osi.GetHostCharacter())
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            return GK.Stage.Spawn{ template = "goblin", at = { %f, %f, %f }, faction = "enemy" }[1] ]], p[1], p[2], p[3])))
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return g
    end)
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(F.uuid(turn.payload.who) == host, "the host has the first turn")
    end)
    local a = step("focus the goblin", "fair", function()
        local answer = game.server(string.format("return GK.Camera.Focus(%q)", goblin), 30)
        check(answer.ok, "the camera settled on the goblin", answer)
        return answer
    end)
    step("in the fight, on the goblin", "observation", function()
        eq(a.fight, true, "the verb saw the fight")
        check(F.flat(a.at, F.where(game, goblin)) <= 0.5, "the camera's target within 0.5 m of the goblin", a.at)
        game.save("settle-fight.txt", string.format("fight: %d server ticks\n", a.serverTicks or -1))
        check(a.serverTicks <= 60, "settled within 60 server ticks", a.serverTicks)
    end)
    local back = step("back to the host", "fair", function()
        local answer = game.server("return GK.Camera.Return()", 30)
        check(answer.ok, "the camera settled on the host", answer)
        return answer
    end)
    step("on the host", "observation", function()
        eq(back.how, "combat target", "the game's restore")
        check(F.flat(back.at, F.where(game, host)) <= 0.5, "the camera's target within 0.5 m of the host", back.at)
    end)
end
