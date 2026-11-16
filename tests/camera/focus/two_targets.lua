-- GK.Camera.Focus given two things frames both, as the game frames a caster and its target on a
-- ranged attack (docs/contracts/lua-api.md, section 22: the combat target's TargetMode 4 with two
-- targets, the camera's distance raised to fit them). Outside a fight the
-- verb writes both into the camera behaviour's targets. Two neutral humans 14 m apart, on either
-- side of a point 10 m to the screen's right, so that neither is on the screen's middle before.
-- Staging (cheat): the camera on the host, the humans. Acting (fair): the focus. Reading
-- (observation): the screen.
-- Run: gatekit --instance <copy> test tests -k camera/focus/two_targets --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A focus on two creatures shows both on the screen." }

local F = require("_focus")

function run(game)
    game.wait_state("Running", "server", 120)
    local pair = step("the camera on the host, two humans 14 m apart", "cheat", function()
        F.home(game)
        local _, a = F.place(game, 3)
        local _, b = F.place(game, 17)
        local out = {}
        for _, p in ipairs({ a, b }) do
            out[#out + 1] = F.uuid(game.server(string.format(
                "return GK.Stage.Spawn{ template = 'human', at = { %f, %f, %f }, faction = 'neutral' }[1]", p[1], p[2], p[3])))
        end
        return out
    end)
    step("the far one is off the screen", "observation", function()
        need(not F.screen(game, F.where(game, pair[2])).inside, "the second human is off the screen before the focus")
    end)
    local a = step("focus both", "fair", function()
        local answer = game.server(string.format("return GK.Camera.Focus({ %q, %q })", pair[1], pair[2]), 30)
        check(answer.ok, "the camera settled with both on the screen", answer)
        return answer
    end)
    step("both on the screen", "observation", function()
        for i, who in ipairs(pair) do
            local s = F.screen(game, F.where(game, who))
            check(s.inside, "human " .. i .. " on the screen", s)
        end
        game.save("settle-two.txt", string.format("two: %d server ticks, how %s\n", a.serverTicks or -1, tostring(a.how)))
        check(a.serverTicks <= 60, "settled within 60 server ticks", a.serverTicks)
    end)
    step("the camera back", "fair", function() F.home(game) end)
end
