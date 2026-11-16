-- A death says who killed: the game_death row's `by` is the killer Osiris KilledBy(victim, owner,
-- attacker, storyAction) names some ticks before Died (Server/GameLog.lua,
-- docs/contracts/lua-api.md, section 21). Staged with a hostile goblin beside the host, killed by
-- damage the host deals it (Osi.ApplyDamage with the host as its source).
-- Staging (cheat): the goblin, the blow. Reading: observation.
-- Run: gatekit --instance <copy> test tests/fight -k fight/log/killed_by

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "A death says who killed." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    local goblin = uuid(step("a goblin beside the host", "cheat", function()
        local g = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            return GK.Stage.Spawn{ template = "goblin", at = { x + 3, y, z }, faction = "enemy" }[1]
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        return g
    end))
    local mark = game.mark()
    step("the host's blow", "cheat", function()
        game.server(string.format("Osi.ApplyDamage(%q, 200, 'Slashing', Osi.GetHostCharacter()) return true", goblin))
    end)
    step("the death names its killer", "observation", function()
        local death = row(mark, "game_death", function(p) return uuid(p.who) == goblin end, 30)
        eq(uuid(death.payload.by), host, "the killer is the host")
    end)
end
