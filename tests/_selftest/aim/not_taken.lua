-- An aim the game does not take leaves nothing behind: after GK.Gui.CancelAim the caster holds
-- no cast on the server, preview or other. The main-hand attack is aimed at a hostile goblin the
-- host cannot reach - far beyond its movement, and just beyond its reach with the movement spent
-- - inside a fight on the host's turn, and left. Whatever the targeting turned into (the cursor's
-- refusal, a walk), the server's spell cast states of the host are read after the cancel.
-- Then the same attack aimed at points on the ground, near and far: an aim that once turned the
-- targeting into a walk (MoveTo) and left the server's preview cast behind, which the hotbar's
-- cancel does not end; GK.Stage.Verify names such a cast.
-- Scene: cheat. Aim and cancel: fair. Reading: observation.

test { level = "GustavX:Basic_Level_A", surprises = { "combat_started" },
           about = "An aim the game does not take leaves no cast of the caster on the server once it is left." }

local CASTS = [[
    local host = Osi.GetHostCharacter()
    local out = {}
    for _, e in ipairs(Ext.Entity.GetAllEntitiesWithComponent("SpellCastState") or {}) do
        local s = e.SpellCastState
        local who = s.Caster and s.Caster.Uuid and s.Caster.Uuid.EntityUuid
        if who and string.find(host, who, 1, true) then
            local options = {}
            for _, f in pairs(s.CastOptions or {}) do options[#options + 1] = tostring(f) end
            out[#out + 1] = { spell = s.SpellId.OriginatorPrototype, options = table.concat(options, "|"), targets = #s.Targets }
        end
    end
    return out
]]

local function place(who, at)
    game.server(string.format([[Osi.TeleportToPosition(%q, %f, %f, %f, "", 0, 0, 0, 0, 1) return true]], who, at[1], at[2], at[3]))
    game.wait("server", string.format("(function() local x, y, z = Osi.GetPosition(%q) return math.abs(x - %f) < 0.05 and math.abs(z - %f) < 0.05 end)()",
        who, at[1], at[3]), 10)
end

function run(game)
    game.wait_state("Running", "server", 120)
    local host = game.server("return Osi.GetHostCharacter()")
    local at = game.server("local x, y, z = Osi.GetPosition(Osi.GetHostCharacter()) return { x, y, z }")

    local staged = game.mark()
    local goblin = step("a hostile goblin, paralysed; the fight on the host's turn", "cheat", function()
        check(game.server([[return GK.Rolls.Pin{ kind = "Initiative", n = 100 }]]).ok, "the host's initiative is pinned")
        local g = game.server(string.format([[return GK.Stage.Spawn{ template = "goblin", at = { %f, %f, %f }, faction = "enemy", exact = true }[1] ]],
            at[1] + 20, at[2], at[3]))
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil", g), 10)
        game.server(string.format("return GK.Stage.Fight({ %q })", g))
        game.server(string.format([[Osi.ApplyStatus(%q, "PARALYZED", -1, 1, Osi.GetHostCharacter()) return true]], g))
        return g
    end)
    step("the host's turn", "observation", function()
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end)
        need(string.find(turn.payload.who, host, 1, true) ~= nil, "the host has the first turn")
    end)

    local left = {}
    local function try(name, target, twice)
        local seen = step(name .. ": aim and leave", "fair", function()
            check(game.ui([[return GK.Gui.UseSlot("Target_MainHandAttack")]]).ok, "the main-hand attack began its targeting")
            if twice then game.ui([[return GK.Gui.UseSlot("Target_MainHandAttack")]]) end
            local aimed = game.ui(string.format("return GK.Gui.Aim(%s)", target or string.format("%q", goblin)))
            local during = game.server(CASTS)
            local cancel = game.ui("return GK.Gui.CancelAim()")
            return { aimed = aimed, cancel = cancel, during = during }
        end)
        step(name .. ": no cast is left on the server", "observation", function()
            local ok = pcall(game.wait, "server", "#(function() " .. CASTS .. " end)() == 0", 5)
            local casts = game.server(CASTS)
            print(string.format("%s: aim %s valid %s mode %s reason %s; during %s; cancel %s; server casts %s", name, tostring(seen.aimed.ok),
                tostring(seen.aimed.valid), tostring(seen.aimed.mode), tostring(seen.aimed.reason), fmt(seen.during), tostring(seen.cancel.did), fmt(casts)))
            if not ok then left[#left + 1] = { name = name, casts = casts, aimed = seen.aimed, cancel = seen.cancel } end
            -- and on the client: no targeting of a spell and no cast entity of the host (the idle
            -- preview of a walk a fight keeps is no spell's)
            local client = game.client([[
                local e = Ext.Entity.GetAllEntitiesWithComponent("ClientControl")[1]
                local ic = e and e.ClientCharacter and e.ClientCharacter.InputController
                local task = ic and ic.PreviewTask
                local ok, spell = pcall(function() return task.Spell.OriginatorPrototype end)
                return { spell = task and ok and spell or false, casting = e and e.SpellCastIsCasting ~= nil or false }
            ]])
            if client.spell or client.casting then left[#left + 1] = { name = name, client = client } end
        end)
    end

    try("far beyond the movement")
    step("the goblin within the movement, beyond reach", "cheat", function() place(goblin, { at[1] + 5, at[2], at[3] }) end)
    try("within the movement, beyond reach")
    try("the slot used twice", nil, true)
    step("no movement left; the goblin just beyond reach", "cheat", function()
        game.server("return GK.Cheat.Charge(Osi.GetHostCharacter(), 'Movement', 100)")
        game.wait("server", "GK.See.Resources(Osi.GetHostCharacter()).movement[1] == 0", 10)
        place(goblin, { at[1] + 2.3, at[2], at[3] })
    end)
    try("just beyond reach, no movement")
    try("the ground beside the host", string.format("{ %f, %f, %f }", at[1] + 1.5, at[2], at[3] + 1))
    try("the ground far away", string.format("{ %f, %f, %f }", at[1] - 8, at[2], at[3] + 6))

    step("nothing was left behind", "observation", function()
        eq(#left, 0, "aims that left a cast on the server: " .. fmt(left))
    end)
end
