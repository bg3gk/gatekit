-- AI.Can and the turn's refusals (mods/AI/README.md, "A turn in a fight"). The rule held here: in
-- a party member's open turn `now.can` names what the game says is left of it (action, bonus,
-- reaction, movement), the enemies the party sees, and the actions of the member's spell book that
-- are ready, grouped by what they spend - an attack with the tooltip's chance and the enemy in its
-- reach now, Dash and Disengage among them - and the opportunity attack
-- that leaving a melee enemy's reach provokes (the game's CombatIsThreatened); the block stays
-- under the hard cap with the room a fight gives `can`. An act the game refuses in the turn says what is left and what still fits,
-- and its line over the head is not shown. With AI.Think.required a step without a reason is
-- refused; the reasons are read back by ai_look's part `thoughts`, which moves no cursor. An
-- examine reads the game's own Examine window, and what it showed of a creature's kind (its AC)
-- stays in the enemies line. A ranged attack in the spell book without a ranged weapon is not
-- ready to cast.
-- Staging (cheat): GK.Fixture.Fight with a melee guard in the host's reach and another at 6 m.
-- Every call of the bridge writes its row, so the steps that call it are cheat steps.
-- Run: gatekit --instance <copy> test tests -k ai/turn --speed working

test { level = "GustavX:Basic_Level_A",
       about = "In a fight the agent is given what is left of its turn and every action it has, unranked." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end
local function has(list, text)
    for _, line in ipairs(list or {}) do if contains(line, text) then return line end end
end
local function ai(game, code, timeout) return game.server("local AI = Mods.GateKitAI.AI; " .. code, timeout or 90) end

function run(game)
    game.wait_state("Running", "server", 120)
    local fight = step("a melee guard in reach and another at 6 m", "cheat", function()
        ai(game, "AI.Session.Start({ mode = 'open', plan = 'off' }) return true")
        return game.server("GK.FixtureF = GK.Fixture.Fight{ provoke = true, enemies = 2 } return GK.FixtureF.entities", 30)
    end)
    local close, far = uuid(fight[1].id), uuid(fight[2].id)
    local first = step("the party's turn opens", "cheat", function()
        game.wait("server", "Mods.GateKitAI.AI.Turn().mine ~= nil", 60)
        return ai(game, "return AI.Look({})")
    end)
    step("can: what is left, the enemies, the actions by what they spend", "cheat", function()
        local can = first.now.can
        check(type(can) == "table" and #can >= 3, "can is told", first.now)
        local left = can[1]
        for _, word in ipairs({ "action 1", "bonus 1", "reaction 1", "movement " }) do
            check(contains(left, word), "left: " .. word, left)
        end
        local res = game.server("return GK.See.Resources(Osi.GetHostCharacter())")
        check(contains(left, string.format("movement %d", math.floor(res.movement[1]))), "the movement is the game's", left, res.movement)
        local enemies = has(can, "enemies: ")
        check(enemies and contains(enemies, "#" .. close:sub(1, 6)) and contains(enemies, "#" .. far:sub(1, 6)), "both guards among the enemies", can)
        local action = has(can, "action: ")
        check(action, "a group of actions that spend the action", can)
        -- the melee entry: the one of 1.5 m that names the guard in reach (the weapon, or the fist)
        local melee
        for entry in (action or ""):gmatch("[^|]+") do
            if contains(entry, " 1.5 m") and contains(entry, "#" .. close:sub(1, 6)) and contains(entry, "Attack") then melee = melee or entry end
        end
        check(melee and melee:match(": %d+%%, in reach #" .. close:sub(1, 6)), "the melee attack: its chance, and the guard in reach now", action)
        check(not contains(melee, "#" .. far:sub(1, 6)), "the far guard is not in reach now (its distance is the enemies line's)", melee)
        check(has(can, "Shout_Dash") and has(can, "Shout_Disengage"), "Dash and Disengage are in the set", can)
        check(not has(can, "best") and not has(can, "instead"), "nothing ranked, nothing advised", can)
        local chars = #game.server("return Mods.GateKitAI.AI.Encode((Mods.GateKitAI.AI.Now({ full = true, peek = true })))")
        check(chars <= game.server("return Mods.GateKitAI.AI.Limit.chars + Mods.GateKitAI.AI.Limit.can"), "the whole fight's block under the hard cap and can's own room", chars)
    end)
    step("examine reads the game's Examine window, and can's enemies line keeps its AC", "cheat", function()
        local seen = ai(game, string.format("return AI.Step({ ['do'] = 'examine', target = %q, think = 'test' })", close), 60)
        check(seen.ok and contains(seen.did, "examined"), "the examine was done", seen)
        local ac = game.server(string.format("return Ext.Entity.Get(%q).Resistances.AC", close))
        check(contains(seen.did, tostring(ac)), "the window's text holds the creature's AC", seen.did, ac)
        local can = ai(game, "return AI.Look({ part = 'can' })").now.can
        check(has(can, "(AC " .. tostring(ac)), "the enemies line keeps what the examine showed", can)
        check(game.ui("return GK.Gui.IsOpen('Examine')") ~= true, "the window is closed again")
    end)
    step("with the custom difficulty's Hide NPC health, the enemies line names no hit points", "cheat", function()
        local set = [[local e = Ext.Entity.GetAllEntitiesWithComponent('RulesetModifiers')[1]
            local id = Mods.GateKitAI.AI.HideNpcHpId e.RulesetModifiers.Modifiers[id] = %s return Mods.GateKitAI.AI.HideNpcHp()]]
        game.server("return Mods.GateKitAI.AI.HideNpcHp()")
        check(game.server(string.format(set, "true")) == true, "the modifier HIDE_NPC_HP is on")
        local hidden = has(ai(game, "return AI.Look({ part = 'can' })").now.can, "enemies: ")
        game.server(string.format(set, "false"))
        check(hidden and not contains(hidden, " hp"), "no enemy's hit points while the game hides them", hidden)
    end)
    step("leaving the guard's reach provokes, and can says so", "cheat", function()
        local threatened = game.server(string.format([[
            for _, h in ipairs(Ext.Entity.Get(Osi.GetHostCharacter()).CombatIsThreatened.ThreatenedBy or {}) do
                if h.Uuid.EntityUuid == %q then return true end
            end
            return false ]], close))
        local line = has(first.now.can, "provokes an opportunity attack")
        if threatened then
            check(line and contains(line, "#" .. close:sub(1, 6)), "the guard in reach is named", first.now.can)
        else
            check(line == nil, "no threat from the game, none told", first.now.can)
        end
    end)
    local mark = game.mark()
    local seq = game.server("return GK.EventLog.Seq")
    local jump = step("a jump far beyond its range, with a line", "cheat", function()
        return ai(game, [[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            return AI.Step({ ['do'] = 'jump', target = string.format('%f,%f,%f', x + 25, y, z), say = 'Over the gap', think = 'testing the reach' }) ]])
    end)
    step("refused with what is left and what fits; the line not shown", "cheat", function()
        check(jump.ok == false, "the jump is refused", jump)
        check(contains(jump.why_not, "left: ") and contains(jump.why_not, "movement "), "why names what is left", jump.why_not)
        check(contains(jump.hint, "fits now: ") or contains(jump.hint, "nothing fits"), "the hint names what still fits", jump.hint)
        local r = row(mark, "ai_step", function(p) return p["do"] == "jump" end, 5)
        eq(r.payload.shown, false, "the row says the line was not shown")
        check(not game.server(string.format("for _, r in ipairs(GK.EventLog.Since(%d, { kinds = { ai_said = true } }).rows) do if r.payload.say == 'Over the gap' then return true end end return false", seq)),
            "no line over the head")
    end)
    step("a reason is required when the setting asks, and read back", "cheat", function()
        local no = ai(game, "AI.Think.required = true return AI.Step({ ['do'] = 'wait' })")
        check(no.ok == false and contains(no.why_not, "needs think") and contains(no.hint, '"think"'), "a step without think is refused", no)
        local yes = ai(game, "return AI.Step({ ['do'] = 'wait', think = 'letting the guards come' })")
        check(yes.ok, "with think it is done", yes)
        ai(game, "AI.Think.required = false return true")
        local before = ai(game, "return AI.Sessions.default.seq")
        local thoughts = ai(game, "return AI.Look({ part = 'thoughts' })")
        check(has(thoughts.now.thoughts, "letting the guards come") and has(thoughts.now.thoughts, "testing the reach (refused)"), "both reasons read back", thoughts.now)
        eq(ai(game, "return AI.Sessions.default.seq"), before, "the look moved no cursor")
    end)
    step("a ranged sneak attack in the spell book without a ranged weapon", "cheat", function()
        local added = game.server([[
            local host, spell = Osi.GetHostCharacter(), 'Projectile_SneakAttack'
            local weapon = Osi.GetEquippedItem(host, 'Ranged Main Weapon')
            if weapon then Osi.Unequip(host, weapon) end
            local added = Osi.HasSpell(host, spell) ~= 1
            if added then Osi.AddSpell(host, spell, 0, 0) end
            return { weapon = weapon, spell = added }
        ]])
        game.wait("server", [[(function()
            local host = Osi.GetHostCharacter()
            return not Osi.GetEquippedItem(host, 'Ranged Main Weapon')
                and #GK.See.Actions(host, nil, { filter = 'Projectile_SneakAttack' }) > 0
        end)()]], 10)
        local can = ai(game, "return AI.Look({ part = 'can' })").now.can
        local cast = ai(game, string.format([[return AI.Step({ ['do'] = 'cast',
            spell = 'Projectile_SneakAttack', target = %q, think = 'test the equipment requirement' })]], close))
        game.server(string.format([[
            local host = Osi.GetHostCharacter()
            if %s then Osi.RemoveSpell(host, 'Projectile_SneakAttack', 0) end
            local weapon = %q
            if weapon ~= '' then Osi.Equip(host, weapon, 1, 0, 0) end
            return true
        ]], tostring(added.spell), added.weapon or ""))
        local missing = has(can, "weapon missing:")
        check(missing and contains(missing, "Projectile_SneakAttack"), "can shows the equipment requirement", can)
        local action = has(can, "action: ")
        check(not action or not contains(action, "Projectile_SneakAttack"), "the unavailable attack is out of the ready actions", can)
        check(cast.ok == false and contains(cast.why_not, "requires an equipped ranged weapon"), "cast reports the missing weapon", cast)
    end)
    step("the fight is cleared", "cheat", function()
        game.server("GK.Fixture.Clear() return true", 30)
    end)
end
