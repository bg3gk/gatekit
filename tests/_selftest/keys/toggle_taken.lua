-- A key pressed with GK.Act.Press is taken by the game every time: twenty presses of the key bound
-- to the weapon toggle (ToggleCombatMode), out of a fight, each followed by the server's
-- `weapon_state` row for the host before the next. A press the game does not take writes no row.
-- Acting: fair. Reading: observation.

test { level = "GustavX:Basic_Level_A",
           about = "Every press of GK.Act.Press reaches the game: each toggle of the weapon writes the server's weapon_state row." }

local PRESSES = 20

function run(game)
    game.wait_state("Running", "server", 120)
    local host = game.server("return Osi.GetHostCharacter()")
    local missed = {}
    for i = 1, PRESSES do
        local mark = game.mark()
        step("press " .. i, "fair", function()
            check(game.ui([[return GK.Act.Press("ToggleCombatMode")]]).ok, "the toggle's key went in")
        end)
        step("press " .. i .. ": the game took it", "observation", function()
            local ok = pcall(row, mark, "weapon_state", function(p) return p.who and string.find(host, p.who, 1, true) ~= nil end, 3)
            if not ok then missed[#missed + 1] = i end
        end)
    end
    step("every press was taken", "observation", function()
        print(string.format("%d of %d presses taken; missed: %s", PRESSES - #missed, PRESSES, fmt(missed)))
        eq(#missed, 0, "presses the game did not take")
    end)
end
