-- What survives a level load: one thing of each kind is changed, the copy's checkpoint is loaded
-- (the reset ladder's `level`), and each is read again. A level load is supposed to rebuild the
-- level from its save; what lives outside the level does not come from the save. The table this
-- prints is the measured half of docs/contracts/lua-api.md 14, "What survives a level load".
-- Needs a checkpoint of the level (`gatekit checkpoint`). Run: gatekit --instance <copy> test tests -k stage_level_survivors

-- reset = "venue": the run then makes a checkpoint of this level at its start, which game.reset() loads
test { level = "GustavX:Basic_Level_A", reset = "venue" }

local BROOM = "cbb98d74-68e9-4d0f-a4ff-6fa92cfcea13"

local READ = string.format([[
local H = Osi.GetHostCharacter()
local e = Ext.Entity.Get(H)
local zero = 0
pcall(function() for _, list in pairs(e.ActionResources.Resources) do for _, r in ipairs(list) do
    if r.MaxAmount > 0 and r.Amount == 0 then zero = zero + 1 end end end end)
return { hp = e.Health.Hp, broom = #(GK.Find(%q, H) or {}), bless = Osi.HasActiveStatus(H, "BLESS"),
         passive = Osi.HasPassive(H, "MAG_Bhalist_Hat_Passive"), resources_at_zero = zero,
         lua_global = GKSurvivalGlobal or "gone", speed = GK.Speed.State().engine,
         switch_AiEnableSwarm = Ext.Utils.GetGlobalSwitches().AiEnableSwarm,
         baseline = GK.Stage.Ledger.baseline ~= nil, x = (GK.Stage.Position() or {})[1] }
]], BROOM)

local CHANGE = string.format([[
local H = Osi.GetHostCharacter()
Osi.ApplyDamage(H, 3, "Fire", "")
Osi.TemplateAddTo(%q, H, 1, 0)
Osi.ApplyStatus(H, "BLESS", -1.0, 1, H)
Osi.AddPassive(H, "MAG_Bhalist_Hat_Passive")
GKSurvivalGlobal = "still here"
GK.Speed.Set(2)
local s = Ext.Utils.GetGlobalSwitches()
GKSwarmWas = s.AiEnableSwarm
s.AiEnableSwarm = not s.AiEnableSwarm
pcall(function() for _, list in pairs(Ext.Entity.Get(H).ActionResources.Resources) do
    for _, r in ipairs(list) do if r.MaxAmount > 0 then r.Amount = 0 end end break end end)
Ext.Entity.Get(H):Replicate("ActionResources")
local x, y, z = Osi.GetPosition(H)
Osi.TeleportToPosition(H, x + 2, y, z + 2, "", 0, 0, 0, 0, 1)
return GKSwarmWas
]], BROOM)

function run(game)
    game.wait_state("Running", "server", 120)
    local before = game.server(READ)
    local swarm = game.server(CHANGE)
    local cap = game.ui([[local c = GK.FrameCap() GKSurvivalCap = c and c.frameCap GK.SetFrameCap(45) return GKSurvivalCap]])
    game.ui([[GKSurvivalClient = "still here" return true]])
    game.sleep(1)
    local changed = game.server(READ)
    local took = game.reset()
    local after = game.server(READ)
    local client = game.ui([[local c = GK.FrameCap() return { cap = c and c.frameCap, lua_global = GKSurvivalClient or "gone" }]])
    print(string.format("level load: %.1f s", took))
    print(string.format("%-22s %-14s %-14s %-14s %s", "what", "before", "changed", "after load", "survives"))
    for _, key in ipairs(keys(before)) do
        local b, c, a = before[key], changed[key], after[key]
        print(string.format("%-22s %-14s %-14s %-14s %s", key, tostring(b), tostring(c), tostring(a),
            (c ~= b and a == c) and "YES" or (c == b and "(not changed)" or "no")))
    end
    print(string.format("%-22s %-14s %-14s %-14s %s", "frame cap (client)", tostring(cap), "45", tostring(client.cap),
        client.cap == 45 and "YES" or "no"))
    print(string.format("%-22s %-14s %-14s %-14s %s", "client Lua global", "gone", "still here", client.lua_global,
        client.lua_global == "still here" and "YES" or "no"))
    -- what survived is put back by hand here, as the ladder does after a load
    game.server(string.format([[Ext.Utils.GetGlobalSwitches().AiEnableSwarm = %s; GK.Speed.Disable() return true]], tostring(swarm)))
    game.ui(string.format([[GK.SetFrameCap(%s) return true]], tostring(cap or 0)))
    game.server([[return GK.Stage.Begin({ fresh = true })]])
end
