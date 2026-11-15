-- A fair melee attack inside a fight leaves nothing behind once the scope is cleaned. Cheat steps
-- stage a hostile goblin beside the host, start the fight, end turns until the host's is active
-- and pin the host's attack roll (GK.Rolls.Pin, kind Attack): at 1 to miss, at 19 to hit. Fair
-- steps attack through the hotbar (GK.Gui.UseSlot, Aim, Confirm). Observation steps read that the
-- attack spent the action point, that the miss took no hit points and the hit did, that the game
-- filled the action again when the host's next turn began, and that GK.Stage.Verify is clean after
-- GK.Stage.Clean. The test after this one gets the plain `fixture` rung of the reset ladder.

test { level = "GustavX:Basic_Level_A",
           about = "A fair attack in a fight spends the action; a pinned miss deals nothing, a pinned hit deals damage; after Clean, Verify is clean." }

local ATTACK = "Target_MainHandAttack"

local function read(game, goblin)
    return game.server(string.format([[
        local host = Osi.GetHostCharacter()
        return { hp = Osi.GetHitpoints(%q), action = (GK.See.Resources(host).action or {})[1],
                 casting = Ext.Entity.Get(host).SpellCastIsCasting ~= nil }
    ]], goblin))
end

-- Ends the turn of whoever is active until it is the host's (the goblin may win the initiative).
local function hostsTurn(game, host)
    local deadline = game.now() + 40
    repeat
        local active = game.server("local c = GK.Stage.Combat() return c and c.active or ''")
        if active == host then return end
        if active ~= "" then game.server(string.format("GK.Cheat.EndTurnOf(%q) return true", active)) end
        game.sleep(0.5)
    until game.now() > deadline
    error("the host's turn did not come within 40 s")
end

-- One fair attack on the goblin; answers the goblin's hit points and the host's action before and after.
local function attack(game, goblin, label)
    local before = step("read before the " .. label, "observation", function() return read(game, goblin) end)
    local mark = game.mark()
    step("attack the goblin (" .. label .. ")", "fair", function()
        local used = game.ui(string.format("return GK.Gui.UseSlot(%q)", ATTACK))
        check(used.ok, "the main-hand attack slot began its targeting", used)
        local aimed = game.ui(string.format("return GK.Gui.Aim(%q)", goblin))
        check(aimed.ok ~= false and aimed.valid ~= false, "the aim is on the goblin", aimed)
        local clicked = game.ui("return GK.Gui.Confirm()")
        check(clicked.ok, "the game took the click", clicked)
    end)
    local after = step("the " .. label .. " is over", "observation", function()
        local deadline = game.now() + 20
        repeat
            local cast = false
            for _, row in ipairs(game.events(mark, { "game_cast" }).rows or {}) do
                if row.payload and row.payload.spell == ATTACK then cast = true end
            end
            local now = read(game, goblin)
            if cast and not now.casting then return now end
            game.sleep(0.2)
        until game.now() > deadline
        error("the attack did not end within 20 s (no game_cast row of " .. ATTACK .. ", or the host still casting)")
    end)
    local damage = {}
    for _, row in ipairs(game.events(mark, { "game_damage" }).rows or {}) do
        if row.payload and row.payload.who == goblin then damage[#damage + 1] = row.payload end
    end
    return before, after, damage
end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = step("read the host", "observation", function()
        return game.server("return Osi.GetHostCharacter()"):match("(%x+%-%x+%-%x+%-%x+%-%x+)$")
    end)

    local goblin = step("stage a hostile goblin", "cheat", function()
        return game.server([[
            local host = Osi.GetHostCharacter()
            GK.Cheat.Protect(true)
            local x, y, z = Osi.GetPosition(host)
            return GK.Cheat.Spawn{ template = "goblin", at = { x + 1.6, y, z }, faction = "enemy" }[1]
        ]])
    end)
    step("the goblin is there", "observation", function()
        game.wait("server", string.format("Ext.Entity.Get(%q) ~= nil and Osi.GetHitpoints(%q) ~= nil", goblin, goblin), 10)
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", goblin), 10)
    end)
    step("start the fight", "cheat", function()
        game.server(string.format("return GK.Cheat.Fight({ %q })", goblin))
    end)
    step("both are in the fight", "observation", function()
        game.wait("server", string.format("Osi.IsInCombat(Osi.GetHostCharacter()) == 1 and Osi.IsInCombat(%q) == 1", goblin), 20)
    end)
    step("the host's turn", "cheat", function() hostsTurn(game, host) end)

    step("pin the attack roll at 1", "cheat", function()
        local pinned = game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'Attack', n = 1 }", host))
        check(pinned.ok and pinned.inForce, "the pin is in force", pinned)
    end)
    local before, after, damage = attack(game, goblin, "miss")
    step("what the miss did", "observation", function()
        print("miss: before " .. fmt(before) .. "  after " .. fmt(after))
        eq(before.action, 1, "the host had its action")
        eq(after.action, 0, "the attack spent the action point")
        eq(after.hp, before.hp, "the miss took no hit points")
        eq(#damage, 0, "the game wrote no damage to the goblin")
    end)

    step("end the turns until the host's comes again; pin at 19", "cheat", function()
        game.server(string.format("GK.Cheat.EndTurnOf(%q) return true", host))
        game.wait("server", string.format("(GK.Stage.Combat() or {}).active ~= %q", host), 10)
        hostsTurn(game, host)
        game.server(string.format("return GK.Rolls.Unpin{ who = %q, kind = 'Attack' }", host))
        local pinned = game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'Attack', n = 19 }", host))
        check(pinned.ok and pinned.inForce, "the pin is in force", pinned)
    end)
    before, after, damage = attack(game, goblin, "hit")
    step("what the hit did", "observation", function()
        print("hit: before " .. fmt(before) .. "  after " .. fmt(after) .. "  damage " .. fmt(damage))
        eq(before.action, 1, "the game filled the action again when the host's turn began")
        eq(after.action, 0, "the attack spent the action point")
        check(after.hp < before.hp, "the hit took hit points", before, after)
        check(#damage > 0, "the game wrote the damage to the goblin", damage)
    end)

    step("clean the scope", "cheat", function()
        local cleaned = game.server("return GK.Stage.Clean()")
        print("clean: " .. fmt(cleaned.did))
        check(#cleaned.notUndone == 0, "Clean undid everything", cleaned.notUndone)
    end)
    step("Verify is clean", "observation", function()
        -- while the game puts its own things back (pending), asked again for up to 12 seconds of
        -- the game's time (Verify carries the game's clock), which holds at any speed
        local verdict = game.server("return GK.Stage.Verify()")
        local since = verdict.time
        while verdict.pending and not verdict.clean and since and verdict.time - since < 12 do
            game.sleep(0.1)
            verdict = game.server("return GK.Stage.Verify()")
        end
        for _, line in ipairs(verdict.kept or {}) do print("kept by the game: " .. line) end
        check(verdict.clean, "Verify is clean after Clean", verdict)
    end)
end
