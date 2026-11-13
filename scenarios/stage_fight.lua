-- GK.Stage: a staged fight from start to finish - spawn enemies with gear, fight, read the
-- turn order, end turns, hit, burn, kill, raise, take the scene down.

scenario { level = "GustavX:TUT_Avernus_C", mods = {} }

function run(game)
    game.wait_state("Running", "server", 60)
    -- A fresh level (and a world just reset) plays its opening cutscene; no fight starts while
    -- one runs. Played twenty times faster it is over in a second.
    game.fast_scenes(20)
    check(game.server("return GK.Stage ~= nil and GK.Stage.Quiet ~= nil"), "the bridge has no GK.Stage")
    check(game.server("return GK.Stage.Quiet()"), "the tutorials could not be marked as seen")
    local before = game.server("return #Ext.Entity.GetAllEntitiesWithComponent('ServerCharacter')")

    local goblins = game.server('return GK.Stage.Spawn{ template = "goblin", count = 2, faction = "enemy", ' ..
        'equipment = { "longsword", "shield" } }')
    check(#goblins == 2, "two goblins expected", goblins)
    game.wait("server", ("GK.Stage.Ready(%s)"):format(lua_list(goblins)), 10)
    -- a goblin comes with a scimitar of its own; the longsword replaces it a moment later
    game.wait("server", ('(GK.Character("%s").equipment["Melee Main Weapon"] or {}).template'):format(goblins[1]) ..
        ':find("Longsword", 1, true) ~= nil', 10)

    game.server(("return GK.Stage.Fight(%s)"):format(lua_list(goblins)))
    -- the fight exists at once, its turn order a moment later
    local fight = game.wait("server", "(GK.Stage.Combat() or {}).active and GK.Stage.Combat()", 20)
    local in_order = {}
    for i, row in ipairs(fight.order) do in_order[i] = row.uuid end
    for _, goblin in ipairs(goblins) do check(has(in_order, goblin), "not in the turn order", goblin, fight) end
    check(fight.round == 1 and fight.active, "the fight did not start in round 1", fight)

    -- the host strikes the first goblin
    local hp = game.server(('return Osi.GetHitpoints("%s")'):format(goblins[1]))
    game.server(('return GK.Stage.Cast("Target_MainHandAttack", "%s")'):format(goblins[1]))
    game.sleep(4.0)
    local struck = game.server(('return Osi.GetHitpoints("%s")'):format(goblins[1]))

    -- turns go round: the active participant changes, the round counter grows
    local actives, rounds = {}, {}
    for _ = 1, 8 do
        actives[tostring(game.server("return GK.Stage.EndTurn()"))] = true
        game.sleep(1.5)
        local now = game.server("return GK.Stage.Combat()")
        if now then rounds[now.round] = true end
    end
    check(count(actives) >= 2, "only one participant ever had a turn", keys(actives))
    check(math.max(table.unpack(keys(rounds))) >= 2, "the fight never left round 1", keys(rounds))

    game.server(('return GK.Stage.Status("BURNING", "%s", 2)'):format(goblins[2]))
    game.sleep(1.0)
    check(has(game.server(('return GK.Stage.Statuses("%s")'):format(goblins[2])), "BURNING"), "not burning")

    game.server(('return GK.Stage.Kill("%s")'):format(goblins[1]))
    game.wait("server", ('Osi.IsDead("%s") == 1'):format(goblins[1]), 10)
    game.server(('return GK.Stage.Resurrect("%s")'):format(goblins[1]))
    game.wait("server", ('Osi.IsDead("%s") == 0'):format(goblins[1]), 15)

    local removed = game.server("return GK.Stage.Reset()")
    check(removed == 2, "the staged characters were not removed", removed)
    game.wait("server", ("#Ext.Entity.GetAllEntitiesWithComponent('ServerCharacter') == %d"):format(before), 15)
    local state = game.server("return GK.Stage.State()")
    check(state.gameState == "Running" and not state.hostDead, "the world is not as it was", state)
    game.fast_scenes(1)
    print(("fight staged: %d in the order, %d took turns over rounds %s, " ..
        "the host's strike took the goblin from %s to %s hit points")
        :format(#in_order, count(actives), join(sorted(keys(rounds))), hp, struck))
end
