-- GK.Gui.Aim / Aiming / Confirm / AimBest and the observed answers of the acting verbs, in a
-- small clean level: a hotbar action by name, an aim at a creature and at a point, the game's own
-- area highlight, the click, and a verb that changes nothing answering so.
-- Staging (cheats, not under test): spells and slots for the host, its initiative pinned (the first
-- turn if the goblins start a fight), three paralysed goblins with
-- an ally beside them.

test { level = "GustavX:Basic_Level_A" }

local function stage()
    return game.server([[
        local host = Osi.GetHostCharacter()
        for _, s in ipairs({ "Projectile_Fireball", "Projectile_FireBolt" }) do GK.Cheat.Teach(s, host) end
        -- through the ledger: Clean takes the boost off, and Verify knows it as the ledger's
        GK.Stage.Track{ kind = "other", note = "spell slots for the host",
                        op = { op = "boost", who = host, text = "ActionResource(SpellSlot,9,3)", cause = "GKStage" } }
        Osi.AddBoosts(host, "ActionResource(SpellSlot,9,3)", "GKStage", host)
        GK.Cheat.Protect(true)
        local hx, hy, hz = Osi.GetPosition(host)
        local cx, cz = hx + 8, hz
        local goblins = GK.Cheat.Spawn{ template = "goblin", count = 3, at = { cx, hy, cz }, faction = "enemy" }
        local offsets = { { 0, -1.8 }, { 1.6, 1.0 }, { -1.6, 1.0 } }
        for i, g in ipairs(goblins) do
            local x, y, z = Osi.FindValidPosition(cx + offsets[i][1], hy, cz + offsets[i][2], 3, g, 1)
            Osi.TeleportToPosition(g, x, y, z, "", 0, 0, 0, 0, 1)
            Osi.ApplyStatus(g, "PARALYZED", -1, 1, host)
        end
        -- 5.3 m from the middle: an aim at the nearest goblin reaches it, the aims between the
        -- goblins do not. At 4.2 m the middle of all three was 3.9 m from it: the game's preview
        -- left it out of `affected` and the Fireball killed it.
        local ally = GK.Cheat.Spawn{ template = "human", at = { cx, hy, cz - 5.3 }, faction = "ally", exact = true }[1]
        Osi.ApplyStatus(ally, "PARALYZED", -1, 1, host)
        return { goblins = goblins, ally = ally, center = { cx, hy, cz } }
    ]])
end

local function hp(ids)
    return game.server(string.format([[local out = {} for _, id in ipairs(%s) do out[#out + 1] = Osi.GetHitpoints(id) end return out]],
        lua_list(ids)))
end

function run(game)
    game.wait_state("Running", "server", 120)
    -- the goblins are hostile and a fight may start on sight: the host has the first turn, so the
    -- clicks are taken whether it starts or not
    local first = game.server("return GK.Rolls.Pin{ who = Osi.GetHostCharacter(), kind = 'Initiative', n = 100 }")
    check(first.ok and first.inForce, "the host's initiative is pinned", first)
    local s = stage()
    game.wait("server", string.format("Ext.Entity.Get(%q) ~= nil", s.ally), 10)

    -- a verb that changes nothing says so
    local nothing = game.ui([[return GK.Cheat.Run("HotBar", "ClearResourceHighlightsCommand")]])
    eq(nothing.ok, false, "a command that changes nothing is not ok")
    eq(nothing.saw, "nothing changed")

    -- with no hotbar action the only targeting is the game's walk cursor: an aim is the walk's
    local idle = game.ui([[return GK.Gui.Aim({ 0, 0, 0 })]])
    check(idle.ok == false or idle.mode == "MoveTo", "an aim with no hotbar action is refused or the walk's", idle)
    game.ui([[return GK.Gui.CancelAim()]])

    -- the hit chance the cursor shows: a number, with the paralysis as an advantage
    check(game.ui([[return GK.Gui.UseSlot("Projectile_FireBolt")]]).ok, "the Fire Bolt slot began its targeting")
    local aimed = game.ui(string.format([[return GK.Gui.Aim(%q)]], s.goblins[1]))
    check(type(aimed.hit_chance) == "number" and aimed.hit_chance > 0, "the aim reads a hit chance", aimed)
    check(aimed.advantages and #aimed.advantages >= 1, "the paralysis is an advantage", aimed)
    eq(aimed.lines[1].text, aimed.hit_chance .. "%", "the hit chance line carries its number")
    eq(aimed.affected[1].hit_chance, aimed.hit_chance, "the creature under the aim has the chance")
    local ground = game.ui(string.format([[return GK.Gui.Aim({ %f, %f, %f })]], s.center[1], s.center[2], s.center[3] + 3))
    eq(ground.hit_chance, nil, "a ground point shows no chance")
    check(game.ui([[return GK.Gui.CancelAim()]]).ok, "the Fire Bolt targeting closed")

    -- the area preview is the game's, and the best aim spares the ally
    local used = game.ui([[return GK.Gui.UseSlot("Projectile_Fireball")]])
    check(used.ok, "the Fireball slot began its targeting", used)
    local best = game.ui([[return GK.Cheat.AimBest{ avoid = "allies" }]], 30)
    check(best.ok and best.best.enemies == 3 and best.best.avoided == 0, "the best aim covers the three goblins and not the ally", best)
    print("best aim: " .. fmt(best.best))
    local all = {}
    for _, g in ipairs(s.goblins) do all[#all + 1] = g end
    all[#all + 1] = s.ally
    local before = hp(all)
    local clicked = game.ui([[return GK.Gui.Confirm()]])
    check(clicked.ok, "the click was taken", clicked)
    game.wait("server", string.format("Osi.GetHitpoints(%q) < %d", s.goblins[1], before[1]), 10)
    local after = hp(all)
    for i = 1, 3 do check(after[i] < before[i], "goblin " .. i .. " took the fire", before, after) end
    eq(after[4], before[4], "the ally took none")
end
