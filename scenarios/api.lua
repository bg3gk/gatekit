-- The GateKit API end to end: read state, give and equip an item, draw weapons, teleport,
-- spawn and remove an object, and see all of it in the event log - no screenshots involved.

scenario { level = "GustavX:TUT_Avernus_C", mods = {} }

function run(game)
    game.wait_state("Running", "server", 60)
    local client = game.client("return GK.Client()")
    check(client.gameState == "Running" and client.controlled, "the client is not playing", client)

    local before = game.server("return GK.Character()")
    check(before.hp > 0 and before.position, "no character to work with", before)
    local mark = game.server("return GK.Events().last")

    -- A shield: give, wait until it is in the inventory, equip, check the slot.
    local shield = game.server('return Ext.Stats.Get("ARM_Shield").RootTemplate')
    game.server(('GK.Give("%s")'):format(shield))
    game.wait("server", ('#GK.Find("%s") > 0'):format(shield), 10)
    local item = game.server(('return GK.Equip("%s")'):format(shield))
    local worn = game.wait("server", 'GK.Character().equipment["Melee Offhand Weapon"]', 10)
    check(worn.uuid == item, "the shield is not in the offhand", worn, item)

    game.server("GK.Unsheath(true)")
    game.wait("server", "GK.Character().unsheathed", 10)
    game.server("GK.Unsheath(false)")

    local x, y, z = before.position[1], before.position[2], before.position[3]
    game.server(("GK.Teleport(%s, %s, %s)"):format(x + 2, y, z))
    local moved = game.wait("server",
        ("math.abs(GK.Character().position[1] - %s) < 0.75 and GK.Character().position"):format(x + 2), 10)

    local spawned = game.server(('return GK.Spawn("%s")'):format(shield))
    check(spawned, "nothing spawned")
    game.server(('GK.Remove("%s")'):format(spawned))

    local events = game.server(("return GK.Events(%s)"):format(mark)).events
    local kinds = {}
    for _, event in ipairs(events) do kinds[event.kind] = true end
    check(kinds.Equipped and kinds.TemplateAddedTo, "events missing", keys(kinds))
    print(("moved to %s; events seen: %s"):format(fmt(moved), join(keys(kinds))))
end
