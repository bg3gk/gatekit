-- A creature that is in a fight already is not surprised by a hit from hiding: the game looks for
-- such a hit only as a creature joins a fight (code:esv::combat::System::CheckForSurprise is called
-- from the start of a fight alone; research/studies/surprised.md). The same arrow that surprises
-- two idle humans (test:surprise/from_hiding) surprises nobody when the two are fighting a third.
-- Staged (cheat): the scene of tests/surprise/_surprise.lua with the host hidden, and a human of the party's
-- allies 3 m beyond the two, whom they fight at once while the hidden host stays out; the attack
-- die pinned to hit; the attack ordered by script. Reading: observation.
-- Limit: 20 s at speed 1 - the three's fight has to begin before the arrow.
-- Run: gatekit --instance <copy> test tests -k surprise/already_fighting --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A hit from hiding does not surprise a creature that is already in a fight." }

local S = require("_surprise")

function run(game)
    S.watch(game)
    local mark = game.mark()
    local cast = step("the host hidden, two hostile humans turned away 8 m off, a friend of the party beyond them", "cheat", function()
        local c = S.scene(game, "hidden", 8)
        local pinned = game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'Attack', n = 15 }", c.host))
        check(pinned.ok and pinned.inForce, "the attack die is pinned", pinned)
        local friend = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            local id = GK.Stage.Spawn{ template = "human", at = { x + 11, y, z + 2 }, faction = "ally", rotation = 270 }[1]
            GK.Stage.Immortal(true, id)
            return id ]])
        c.friend = S.uuid(friend)
        return c
    end)
    step("the two fight the friend, the hidden host is in no fight", "observation", function()
        S.joined(mark, cast.target)
        S.joined(mark, cast.ally)
        S.joined(mark, cast.friend)
        -- the fight's first turn has begun: an attack ordered while the fight is still opening
        -- was not carried out at speed 1
        row(mark, "game_turn", function(p) return p.who ~= nil end, 60)
        need(game.server(string.format("return Osi.IsInCombat(%q)", cast.host)) == 0, "the hidden host is in no fight")
        need(game.server(string.format("return Osi.HasActiveStatus(%q, 'SNEAKING')", cast.host)) == 1, "the host is still hidden")
    end)
    local shot = game.mark()
    step("the hidden host shoots the target", "cheat", function() S.attack(game, cast, cast.target, "ranged") end)
    step("the arrow hits, the host joins the fight, nobody is surprised", "observation", function()
        row(shot, "game_hit", function(p) return S.uuid(p.who) == cast.target and S.uuid(p.by) == cast.host end, 60)
        local joined = S.joined(shot, cast.host)
        check(joined.payload.underway == true, "the host joined a fight under way", joined.payload)
        row(shot, "turn_ended", function(p) return p.who ~= nil end, 60)
        for _, r in ipairs(game.events(mark, { "game_status" }).rows or {}) do
            check(r.payload.status ~= "SURPRISED", "somebody became surprised", r.payload)
        end
        eq(game.server(string.format("return Osi.HasActiveStatus(%q, 'SURPRISED')", cast.target)), 0, "the target holds the status")
    end)
end
