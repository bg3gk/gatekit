-- What a step's class refuses, and an unmet precondition. A fair step that calls the scripted
-- attack (GK.Cheat.Attack) fails with "a fair step called the cheat GK.Cheat.Attack", also when
-- the chunk catches the refusal itself, and the cheat does not run; an observation step may not
-- press a key; GK.Act.Attack,
-- whose player's path is not built, answers with the route and the cheat and does nothing. The
-- test then ends on a need() that does not hold: its outcome is UNMET, not a pass or a failure.

test { level = "GustavX:Basic_Level_A" }

local function refused(name, class, fn)
    local ok, problem = pcall(step, name, class, fn)
    check(not ok, "step " .. name .. " was not refused")
    return tostring(problem)
end

function run(game)
    game.wait_state("Running", "server", 120)

    local target, cheatsBefore = step("pick a target", "observation", function()
        return game.server("return Osi.GetHostCharacter()"), #game.server("return GK.Cheat.Used()")
    end)

    local why = refused("scripted attack", "fair", function()
        game.server(string.format("return GK.Cheat.Attack(nil, %q)", target))
    end)
    check(contains(why, "step scripted attack (fair) failed"), "the failure names its step", why)
    check(contains(why, "a fair step called the cheat GK.Cheat.Attack"), "the refusal names the cheat", why)

    why = refused("caught scripted attack", "fair", function()
        game.server(string.format("local ok = pcall(GK.Cheat.Attack, nil, %q) return 'went on'", target))
    end)
    check(contains(why, "a fair step called the cheat GK.Cheat.Attack"), "a refusal the chunk caught still fails it", why)

    why = refused("a key in an observation", "observation", function()
        game.client([[return GK.Key("escape")]])
    end)
    check(contains(why, "an observation step called the fair GK.Key"), "an observation step refuses a fair verb", why)

    local answer = step("the fair name refuses", "fair", function()
        return game.server(string.format("return GK.Act.Attack(nil, %q)", target))   -- the refusal is what is tested
    end)
    eq(answer.ok, false, "GK.Act.Attack does nothing")
    eq(answer.cheat, "GK.Cheat.Attack", "it names the scripted attack")
    check(contains(answer.error, "GK.Gui.UseSlot"), "it names the player's route", answer)

    local cheats = step("a refused cheat did not run", "observation", function()
        return game.server([[return GK.Cheat.Used()]])
    end)
    eq(#cheats, cheatsBefore, "no cheat ran: the record of cheats is as it was")

    need(false, "a precondition this test cannot have: the outcome is unmet")
end
