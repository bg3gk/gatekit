-- Enemies that sleep do not fight: beyond the activation manager's sleeping distance of the party
-- (research/studies/camera-focus.md, "Who wakes the space") two creatures made hostile to each
-- other stand side by side and no fight begins. Woken as a place (GK.Wake.Place, docs/contracts/
-- lua-api.md, section 23) they open their fight at once, with neither the host nor the place's
-- helper - a member of the party's view, off stage - taken in. A fight then keeps its members
-- awake by itself: the place released, the release finds both still active, and the fight goes on
-- into further rounds.
-- Stands on test:fight/npc/without_the_party (such a fight within the party's reach, and its
-- death) and test:wake/place (the place woken).
-- Staged with a goblin and a human 3 m apart, 5 m beyond the sleeping distance, both missing (the
-- attack die pinned at 1): nobody is hurt, and the clean ends the fight by removing the two.
-- "No fight" is held for 90 server ticks; awake, the fight is joined within a few.
-- Staging (cheat): the pair, the pins, the factions, the wake and its release.
-- Reading: observation.
-- Limit: 30 s at speed 1 - a newborn's sleep, the 90 ticks of no fight, the release's own 90
-- ticks of waiting for a sleep that does not come, a round of the fight.
-- Run: gatekit --instance <copy> test tests -k fight/npc/asleep --speed working

test { level = "GustavX:Basic_Level_A", surprises = { "combat_elsewhere" },
       about = "Sleeping enemies start no fight; woken as a place they fight with no party member in it, and the fight keeps them awake when the place is released." }

local N = require("_npc")

local function active(game, who)
    return game.server(string.format("local e = Ext.Entity.Get(%q) return e ~= nil and e.Active ~= nil", who))
end

function run(game)
    game.wait_state("Running", "server", 120)
    local radii = step("the manager's distances, the fight log on", "observation", function()
        local r = game.server("return GK.Wake.Radii()")
        need(r.ok, "the activation manager's distances can be read (GK.Wake.Radii)")
        game.server("return GK.EventLog.On('fight', { seconds = 600 })")
        return r
    end)
    local cast = step("a goblin and a human beyond the sleeping distance, both missing", "cheat", function()
        local c = N.pair(game, radii.sleep + 5, { goblin = 1, human = 1 })
        game.wait("server", string.format("not Ext.Entity.Get(%q).Active and not Ext.Entity.Get(%q).Active", c.goblin, c.human), 10)
        return c
    end)
    local staged = game.mark()
    step("the two turn hostile to each other", "cheat", function() N.hostile(game, cast) end)
    step("asleep, they start no fight", "observation", function()
        local from = game.server("return GK.Speed.Clock().ticks")
        game.wait("server", string.format("GK.Speed.Clock().ticks >= %d", from + 90), 30)
        need(not active(game, cast.goblin) and not active(game, cast.human), "both are still asleep")
        eq(#(game.events(staged, { "fight_joined" }).rows or {}), 0, "creatures that joined a fight while asleep")
        N.never_joined(game, cast.goblin, staged, "the sleeping goblin")
        N.never_joined(game, cast.human, staged, "the sleeping human")
    end)
    local waking = game.mark()
    local woke = step("wake the place around them", "cheat", function()
        local a = game.server(string.format("return GK.Wake.Place(%q)", cast.goblin), 30)
        check(a.ok, "the place is awake", a)
        return a
    end)
    local combat = step("awake, they fight, without the host and without the helper", "observation", function()
        local id = N.joined(game, cast, waking)
        row(waking, "combat_elsewhere", function(p) return p.combat == id end, 30)
        row(waking, "fight_action", function(p) return N.uuid(p.who) == cast.goblin or N.uuid(p.who) == cast.human end, 60)
        N.never_joined(game, cast.host, staged, "the host")
        N.never_joined(game, N.uuid(woke.helper), staged, "the place's helper")
        return id
    end)
    local released = game.mark()
    step("let the place go", "cheat", function()
        local a = game.server(string.format("return GK.Wake.Release(%q)", woke.helper), 30)
        -- The release waits for those it kept awake to sleep, and these two do not.
        eq(a.sleptCount, 0, "creatures that fell asleep at the release")
        check(a.ok == false and a.stillAwake ~= nil and #a.stillAwake == 2, "the release names both as still awake", a)
    end)
    step("the fight keeps them awake and goes on", "observation", function()
        local from = row(released, "round_started", function(p) return p.combat == combat end, 60).payload.round
        row(released, "round_started", function(p) return p.combat == combat and p.round == from + 1 end, 60)
        check(active(game, cast.goblin) and active(game, cast.human), "both are active with no anchor in reach")
        local rule = game.server(string.format("return GK.Wake.State(%q).rule", cast.goblin))
        eq(rule, "none", "the rule the toolkit reads for the goblin's being awake")
        N.never_joined(game, cast.host, staged, "the host")
    end)
end
