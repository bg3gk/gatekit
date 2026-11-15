-- A creature a scope staged outlives no Clean: a fresh GK.Stage.Begin over a scope that was never
-- cleaned does not take it into the new baseline. The cheat steps stage a goblin and open a fresh
-- scope over it; the observation steps read that Begin names it as a stray, that Verify names it
-- as an object the baseline did not hold, and that after Clean it is gone and Verify is clean.

test { level = "GustavX:Basic_Level_A",
           about = "A goblin staged in a scope that was never cleaned is not part of the next baseline: Verify names it, Clean removes it." }

function run(game)
    game.wait_state("Running", "server", 120)
    local goblin = step("stage a goblin", "cheat", function()
        return game.server([[
            local host = Osi.GetHostCharacter()
            local x, y, z = Osi.GetPosition(host)
            return GK.Cheat.Spawn{ template = "goblin", at = { x + 3, y, z }, faction = "neutral" }[1]
        ]])
    end)
    step("the goblin is there", "observation", function()
        game.wait("server", string.format("Ext.Entity.Get(%q) ~= nil", goblin), 10)
    end)
    local begun = step("a fresh scope over it, with no Clean", "cheat", function()
        return game.server("return GK.Stage.Begin({ fresh = true })")
    end)
    step("Begin names the stray and Verify names the goblin", "observation", function()
        check(has(begun.strays or {}, goblin), "Begin carried the goblin as a stray", begun)
        local verdict = game.server("return GK.Stage.Verify()")
        local named = false
        for _, line in ipairs(verdict.differences or {}) do
            if contains(line, "not there") and contains(line, goblin) then named = true end
        end
        check(not verdict.clean and named, "Verify names the goblin the baseline did not hold", verdict)
    end)
    step("clean the scope", "cheat", function()
        local cleaned = game.server("return GK.Stage.Clean()")
        check(#cleaned.notUndone == 0, "Clean undid everything", cleaned.notUndone)
    end)
    step("the goblin is gone and Verify is clean", "observation", function()
        local verdict = game.server("return GK.Stage.Verify()")
        local since = verdict.time
        while verdict.pending and not verdict.clean and since and verdict.time - since < 12 do
            game.sleep(0.1)
            verdict = game.server("return GK.Stage.Verify()")
        end
        check(verdict.clean, "Verify is clean after Clean", verdict)
        eq(game.server(string.format("return Ext.Entity.Get(%q) == nil", goblin)), true, "the goblin is gone")
    end)
end
