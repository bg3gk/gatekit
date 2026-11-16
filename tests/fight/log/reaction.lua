-- A reaction is in the fight log with what set it off: a creature that leaves a hostile's melee
-- reach provokes an attack of opportunity (bg3.wiki, "Attack of Opportunity"); the game's AI takes
-- it by itself, and the `fight_reaction` row (Osiris ReactionInterruptUsed) names the one who
-- reacts, the interrupt, the mover and where it left the reach (esv::move::LeaveAttackRangeEvent),
-- and the id of the reaction's cast, whose `fight_action` row says it is a reaction
-- (docs/contracts/lua-api.md, "The family fight"; the family `fight`, Server/FightLog.lua). Staged with a
-- goblin beside the host on the host's turn; the host walks away with GK.Cheat.Move told to
-- provoke. The first attack of opportunity of a game start calls up the tutorial "Attack of
-- Opportunity" on the client, which waits for the player: it is closed as a player closes it.
-- Staging (cheat): the host kept alive, the pin, the goblin, the fight, the walk. Closing the
-- tutorial is the player's (fair). Reading: observation.
-- Run: gatekit --instance <copy> test tests/fight -k fight/log/reaction

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started", "tutorial" },
           about = "An attack of opportunity is in the fight log with the creature that provoked it." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = uuid(game.server("return Osi.GetHostCharacter()"))
    step("the fight log on", "observation", function()
        game.server("return GK.EventLog.On('fight', { seconds = 600 })")
    end)
    local staged = game.mark()
    local goblin = step("a goblin beside the host, the host first, the fight", "cheat", function()
        local g = game.server([[
            local h = Osi.GetHostCharacter()
            GK.Stage.Immortal(true, h)
            GK.Rolls.Pin{ kind = "Initiative", n = 100 }
            local x, y, z = Osi.GetPosition(h)
            return GK.Stage.Spawn{ template = "goblin", at = { x + 1.6, y, z }, faction = "enemy" }[1]
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        return uuid(g)
    end)
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
    local mark = game.mark()
    step("the host walks out of the goblin's reach", "cheat", function()
        local m = game.server("local h = Osi.GetHostCharacter() local x, y, z = Osi.GetPosition(h) return GK.Cheat.Move(h, { x - 6, y, z }, { provoke = true })")
        check(m and m.job, "the walk is asked", m)
    end)
    local reaction = step("the goblin's attack of opportunity, and why", "observation", function()
        local r = row(mark, "fight_reaction", function(p) return uuid(p.who) == goblin end, 30).payload
        eq(r.interrupt, "Interrupt_AttackOfOpportunity", "the interrupt")
        eq(r.auto, true, "the goblin took it by itself")
        check(r.trigger ~= nil, "the row names what set it off", r)
        eq(uuid(r.trigger.mover), host, "the host left the goblin's reach")
        check(r.trigger.where ~= nil, "and where", r.trigger)
        local a = row(mark, "fight_action", function(p) return uuid(p.who) == goblin end, 5).payload
        eq(a.reaction, true, "the goblin's cast is a reaction")
        eq(a.cast, r.cast, "the reaction row names that cast")
        return r
    end)
    step("the tutorial closed, if the game showed it", "fair", function()
        -- The tutorial shows once per game start, as the reaction is used; the goblin's attack is
        -- judged whether it shows or not. The first of the two rows ends the wait, so a run that
        -- sees no tutorial waits for nothing but the attack's own row.
        local first = game.wait_row(mark, { "tutorial", "game_hit" }, function(p, r)
            return r.kind == "tutorial" or (uuid(p.who) == host and uuid(p.by) == goblin)
        end, 30)
        if first.kind == "tutorial" or game.ui("return GK.Gui.Blocking().window") == "tutorial" then
            check(game.ui("return GK.Gui.Resolve{ tutorial = 'close' }").ok, "the tutorial is closed")
        end
    end)
end
