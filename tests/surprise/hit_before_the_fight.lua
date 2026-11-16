-- A hit from hiding surprises only when it lands before its target joins the fight: the game looks
-- for such a hit at the moment of the joining and never again (code:esv::combat::System::CheckForSurprise;
-- research/studies/surprised.md). An invisible attacker shows it both ways, because the status
-- INVISIBLE leaves as the attack is made and the two then see their attacker: a blade is on the
-- target before that - the hit, then the fight, the target and its ally surprised - while an arrow
-- shot from 8 m is still in the air when the fight begins by sight - the fight, then the hit, and
-- nobody surprised, though the hit is as much "from hiding" as the blade's. bg3.wiki, "Surprised
-- (condition)", calls an attack from invisibility the most reliable way to surprise, and lists
-- melee as always working and ranged attacks as working at melee range.
-- Staged (cheat): the scene of tests/surprise/_surprise.lua, beside the host for the blade and 8 m off for the
-- arrow; the attack die pinned to hit; the attack ordered by script. Reading: observation.
-- The ceiling: the arrow's flight is game time and the server's ticks are not. At speed 1 the
-- arrow flies 8 m in six ticks and the fight begins at their first; at speed 8 it flies in less
-- than one tick, the hit is on the target a tick before the joining, and the same shot surprises
-- both (research/studies/surprised.md, "The arrow's flight and the game's speed").
-- Run: gatekit --instance <copy> test tests -k surprise/hit_before_the_fight --speed 1

test { level = "GustavX:Basic_Level_A", speed_ceiling = 1,
       ceiling_finding = "research/studies/surprised.md, The arrow's flight and the game's speed",
       about = "An invisible attacker's blade lands before the fight and surprises; its arrow from 8 m lands after the fight began and surprises nobody." }

local S = require("_surprise")

local function stage(game, metres)
    S.watch(game)
    local cast = step(string.format("the host invisible, two hostile humans turned away %.1f m off", metres), "cheat", function()
        local c = S.scene(game, "invisible", metres)
        local pinned = game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'Attack', n = 15 }", c.host))
        check(pinned.ok and pinned.inForce, "the attack die is pinned", pinned)
        return c
    end)
    step("the scene is as staged", "observation", function() S.quiet(game, cast) end)
    return cast
end

case("a blade", function(game)
    local cast = stage(game, 1.6)
    local mark = game.mark()
    step("the invisible host stabs the target", "cheat", function() S.attack(game, cast, cast.target, "melee") end)
    step("the hit, then the fight: the target and its ally surprised", "observation", function()
        local hit = row(mark, "game_hit", function(p) return S.uuid(p.who) == cast.target and S.uuid(p.by) == cast.host end, 60)
        local joined = S.joined(mark, cast.target)
        check(hit.tick <= joined.tick, "the hit is not later than the joining", hit.tick, joined.tick)
        S.surprised(mark, cast.target)
        S.surprised(mark, cast.ally)
    end)
end)

case("an arrow from 8 m", function(game)
    local cast = stage(game, 8)
    local mark = game.mark()
    step("the invisible host shoots the target", "cheat", function() S.attack(game, cast, cast.target, "ranged") end)
    step("the fight, then the hit: nobody surprised", "observation", function()
        local joined = S.joined(mark, cast.target)
        local hit = row(mark, "game_hit", function(p) return S.uuid(p.who) == cast.target and S.uuid(p.by) == cast.host end, 60)
        check(joined.tick < hit.tick, "the joining is earlier than the hit", joined.tick, hit.tick)
        S.never_surprised(game, mark, cast.target, "the target")
        S.never_surprised(game, mark, cast.ally, "the ally")
    end)
end)
