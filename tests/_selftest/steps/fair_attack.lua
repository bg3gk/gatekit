-- Named steps of three classes around one attack made the player's way: a cheat step stages a
-- goblin beside the host, a fair step attacks it through the hotbar (GK.Gui.UseSlot on the
-- main-hand attack, GK.Gui.Aim at it, GK.Gui.Confirm), and observation steps read what the game
-- wrote in its combat log. The goblin is neutral, so the attack is made outside a fight, where
-- the game charges no action - unless the hostility the attack declares starts the fight first,
-- and the attack is then made on the host's turn. Every chunk a step sends carries its class.

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)

    local goblin = step("stage a goblin", "cheat", function()
        return game.server([[
            local host = Osi.GetHostCharacter()
            GK.Cheat.Protect(true)
            local x, y, z = Osi.GetPosition(host)
            -- neutral: no fight starts before the attack does (an enemy in sight starts one, and
            -- the host may then not have the first turn)
            local g = GK.Cheat.Spawn{ template = "goblin", at = { x + 1.6, y, z }, faction = "neutral" }[1]
            return g
        ]])
    end)
    step("the goblin is there", "observation", function()
        game.wait("server", string.format("Ext.Entity.Get(%q) ~= nil and Osi.GetHitpoints(%q) ~= nil", goblin, goblin), 10)
        -- the aim is the client's: the creature must exist there too
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", goblin), 10)
    end)
    -- paralysed: it cannot answer the attack with the game's assault dialogue before the blow
    -- lands, which cancelled the attack in some runs
    step("paralyse the goblin", "cheat", function()
        game.server(string.format("return GK.Cheat.Status('PARALYZED', %q, -1)", goblin))
        game.wait("server", string.format("Osi.HasActiveStatus(%q, 'PARALYZED') == 1", goblin), 10)
    end)

    local mark = game.mark()
    local before = step("read the host before", "observation", function()
        return game.server(string.format([[
            local host = Osi.GetHostCharacter()
            return { hp = Osi.GetHitpoints(%q), action = (GK.See.Resources(host).action or {})[1] }
        ]], goblin))
    end)

    -- An attack declared on a neutral creature makes it hostile: the game may answer with its
    -- assault dialogue and a fight before the blow lands, which closes the targeting or cancels
    -- the attack. Then, as a player would, the attack is made again on the host's turn.
    local HOSTS_TURN = "(GK.Stage.Combat() or {}).active == Osi.GetHostCharacter() and not GK.InDialog(Osi.GetHostCharacter())"
    local rows
    for attempt = 1, 2 do
        local label = attempt == 1 and "" or " again, on the host's turn"
        step("attack the goblin" .. label, "fair", function()
            if attempt == 2 then
                game.wait("server", HOSTS_TURN, 40)
                -- an action spent on an attack that began the fight is not given back on the first
                -- turn (bg3.wiki, "Resources"): the turn is ended, and the next one has it
                if (game.server("return (GK.See.Resources(Osi.GetHostCharacter()).action or { 0 })[1]") or 0) < 1 then
                    game.server("return GK.Act.EndTurn()")
                    game.wait("server", "(GK.Stage.Combat() or {}).active ~= Osi.GetHostCharacter()", 20)
                    game.wait("server", HOSTS_TURN, 60)
                end
            end
            local used = game.ui([[return GK.Gui.UseSlot("Target_MainHandAttack")]])
            check(used.ok, "the main-hand attack slot began its targeting", used)
            local aimed = game.ui(string.format([[return GK.Gui.Aim(%q)]], goblin))
            if attempt == 1 and (aimed.ok == false or aimed.valid == false)
                and game.server("return Osi.IsInCombat(Osi.GetHostCharacter()) == 1") then
                game.ui("return GK.Gui.CancelAim()")
                return
            end
            check(aimed.ok ~= false and aimed.valid ~= false, "the aim is on the goblin", aimed)
            local clicked = game.ui([[return GK.Gui.Confirm()]])
            check(clicked.ok, "the game took the click", clicked)
        end)
        rows = step("the attack in the combat log" .. label, "observation", function()
            local deadline = game.now() + 15
            repeat
                local got = game.events(mark, { "game_roll", "game_damage" })
                if #got.rows > 0 then return got.rows end
                -- the fight the hostility began came first and it is the host's turn: no blow is coming
                if attempt == 1 and game.server("return " .. HOSTS_TURN) then return nil end
                game.sleep(0.3)
            until game.now() > deadline
            error("no attack roll or damage in the game log within 15 s")
        end)
        if rows then break end
    end
    check(rows, "the attack reached the combat log")

    step("what the attack did", "observation", function()
        local after = game.server(string.format([[
            local host = Osi.GetHostCharacter()
            return { hp = Osi.GetHitpoints(%q), action = (GK.See.Resources(host).action or {})[1] }
        ]], goblin))
        print("combat log: " .. fmt(rows[1].payload))
        print("before: " .. fmt(before) .. "  after: " .. fmt(after))
        local kinds = {}
        for _, row in ipairs(rows) do kinds[#kinds + 1] = row.kind end
        check(has(kinds, "game_roll") or has(kinds, "game_damage"), "the game wrote the attack", kinds)
        -- a hit takes hit points; a miss (the cursor showed a chance below 100) takes none and is
        -- written as a roll
        check(after.hp < before.hp or has(kinds, "game_roll"), "the attack hit, or the roll says it missed", before, after)
    end)
end
