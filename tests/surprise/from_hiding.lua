-- The game itself, with no script, gives SURPRISED to a creature that joins a fight while a hit
-- made from hiding rests on it, and to every ally of it in that fight (research/studies/surprised.md;
-- code:esv::combat::System::CheckForSurprise, code:esv::combat::SurprisedSystem::Update). bg3.wiki,
-- "Turn-based mode", says the attacked creature is surprised when it is unaware of its attacker; the
-- page "Surprised (condition)" adds that a miss counts as a hit does.
-- Cases: the hidden host's arrow hits; it misses (the attack die pinned at 1) - the status comes all
-- the same; the host is not hidden - the two notice it at once, the fight begins by sight, the
-- arrow never flies and nobody is surprised.
-- Staged (cheat): the scene of tests/surprise/_surprise.lua 8 m from the host, the attack die, the attack
-- ordered by script (GK.Cheat.Attack: the rule is about the hit, not the hand that made it).
-- Reading (observation): the game's own rows - the hit, the joining, the status with no giver.
-- Run: gatekit --instance <copy> test tests -k surprise/from_hiding --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A hit from hiding that lands before the fight surprises its target and the target's allies in the fight; a miss does as well; without hiding nobody is surprised." }

local S = require("_surprise")

local function opener(die)
    return function(game)
        S.watch(game)
        local cast = step("the host hidden, two hostile humans turned away 8 m off", "cheat", function()
            local c = S.scene(game, "hidden", 8)
            local pinned = game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'Attack', n = %d }", c.host, die))
            check(pinned.ok and pinned.inForce, "the attack die is pinned", pinned)
            return c
        end)
        step("the scene is as staged", "observation", function() S.quiet(game, cast) end)
        local mark = game.mark()
        step("the hidden host shoots the target", "cheat", function() S.attack(game, cast, cast.target, "ranged") end)
        step("the hit comes first, then the fight, then the status on the target and on its ally", "observation", function()
            local hit = row(mark, "game_hit", function(p) return S.uuid(p.who) == cast.target and S.uuid(p.by) == cast.host end, 60)
            eq(hit.payload.outcome, die == 1 and "critical miss" or "hit", "the arrow's outcome")
            local joined = S.joined(mark, cast.target)
            check(hit.tick <= joined.tick, "the hit is not later than the target's joining", hit.tick, joined.tick)
            S.joined(mark, cast.ally)
            local t = S.surprised(mark, cast.target)
            local a = S.surprised(mark, cast.ally)
            check(t.tick - joined.tick <= 10 and a.tick - joined.tick <= 10, "the status comes within ten ticks of the joining",
                  joined.tick, t.tick, a.tick)
            eq(game.server(string.format("return Osi.HasActiveStatus(%q, 'SURPRISED')", cast.host)), 0, "the host holds the status")
        end)
    end
end

case("the arrow hits", opener(15))
case("the arrow misses", opener(1))

case("not hidden", function(game)
    S.watch(game)
    local mark = game.mark()
    local cast = step("two hostile humans turned away 8 m off, the host in plain sight", "cheat", function()
        return S.scene(game, "plain", 8)
    end)
    step("the fight begins by sight and nobody is surprised", "observation", function()
        S.joined(mark, cast.target)
        S.joined(mark, cast.ally)
        S.never_surprised(game, mark, cast.target, "the target")
        S.never_surprised(game, mark, cast.ally, "the ally")
    end)
end)
