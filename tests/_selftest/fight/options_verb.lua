-- What a creature could use on a target now, for a creature the game's AI is not deciding for:
-- GK.Fight.Options reads the creature's spell book, each spell's costs against its action
-- resources, its reach against the distance, and the game's chance of its roll
-- (docs/contracts/lua-api.md, section 21; Server/Fight.lua). It is GateKit's reading, not the
-- game's can-cast. Staged with a neutral goblin five metres from the host, out of a fight: the
-- host's bow reaches it, its melee weapon does not.
-- Staging (cheat): the goblin. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k _selftest/fight/options_verb

test { level = "GustavX:Basic_Level_A",
           about = "The spells a creature could use on a target now are read with their cost, their reach and the game's chance." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    local goblin = step("a neutral goblin five metres off", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            local x, y, z = Osi.GetPosition(h)
            return GK.Stage.Spawn{ template = "goblin", at = { x + 5, y, z }, faction = "neutral" }[1]
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        return uuid(g)
    end)
    step("the host's options against it", "observation", function()
        local r = game.server(string.format("return GK.Fight.Options(%q, %q)", host, goblin))
        check(r.ok, "the verb answers", r)
        check(r.distance > 4 and r.distance < 6, "the distance is the one staged", r.distance)
        local by = {}
        for _, o in ipairs(r.options) do by[o.spell] = o end
        local bow, blade = by["Projectile_MainHandAttack"], by["Target_MainHandAttack"]
        check(bow ~= nil and blade ~= nil, "the spell book holds both weapon attacks", keys(by))
        check(bow.affordable == true and bow.costs and bow.costs.ActionPoint == 1, "an attack costs an action, and the host has one", bow)
        check(bow.inRange == true, "the bow reaches five metres", bow)
        check(blade.inRange == false, "the melee weapon does not", blade)
        for _, o in ipairs(r.options) do
            if o.chance ~= nil then check(o.chance >= 0 and o.chance <= 1, "a chance is between 0 and 1", o) end
        end
        check(bow.chance ~= nil, "the game gives a chance of the bow's attack", bow)
    end)
end
