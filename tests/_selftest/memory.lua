-- GK.Memory: the memory instrument. In the sandbox level, on two unlike fields - a character's hit
-- points and its position while it walks - watch which instruction writes the field, then freeze the
-- hit points and show a clean takes the freeze away. Every step is a cheat or an observation, never
-- fair. Then who reads the hit points, a watch of each side's own. The whole thing needs GateKit's
-- Script Extender (v34-instruments) in developer mode; on any other build the test passes and
-- says it skipped.
--
--     gatekit --instance <yours> test tests -k memory

test { level = "GustavX:Basic_Level_A" }

function run(game)
    game.wait_state("Running", "server", 120)

    local available = game.server([[return GK.Memory ~= nil and GK.Memory.Available()]])
    if not available then
        print("skipped: GK.Memory needs GateKit's Script Extender (v34-memory) in developer mode")
        return
    end

    -- Every verb is classed; none is fair.
    local classes = game.server([[return GK.Memory.Class]])
    check(classes.Write == "cheat" and classes.Freeze == "cheat", "a write or a freeze is not marked a cheat", classes)
    check(classes.Watch == "observation" and classes.Field == "observation", "watching or reading is not an observation", classes)

    -- (a) Who writes the hit points: watch Health.Hp, deal damage, read the hit.
    local a = game.server([[
        local host = Osi.GetHostCharacter()
        GK.Memory.ClearWatch(); GK.Cheat.Protect(false)
        if Osi.IsDead(host) == 1 then GK.Cheat.Resurrect(host) end
        local f = GK.Memory.Field(host, "eoc::HealthComponent", "Hp")
        Mods.GateKit._mtest = { host = host, hp = f }
        GK.Memory.Watch(f)
        GK.Stage.Damage(5, host, "Bludgeoning", true)
        return { addr = f.Address, before = GK.Memory.Read(f.Address, "int32") }
    ]])
    check(a.addr ~= nil and a.addr % 4 == 0, "the Hp field address is missing or unaligned", a)

    -- The write lands on a later tick; wait for a hit (Waiting counts them; it does not drain).
    game.wait("server", "GK.Memory.Waiting() >= 1", 10)
    local hitA = game.server([[
        local hits = GK.Memory.Hits()
        local h = hits[1]
        GK.Memory.ClearWatch()
        if not h then return { none = true } end
        return { module = h.NextInstruction.Module, rva = h.NextInstruction.RVA, thread = h.Thread,
                 before = h.Before, after = h.After, changed = h.Changed,
                 ret = (h.Return[2] and { h.Return[2].Module, h.Return[2].RVA }) or nil }
    ]])
    check(not hitA.none, "no write to Hp was caught", hitA)
    -- the damage is written within the chunk: what it read is the value after
    check(hitA.changed and hitA.after == a.before and hitA.before == a.before + 5,
          "the hit holds the value before the write and after it", { a = a, hit = hitA })
    check(hitA.module == "bg3_dx11.exe" or hitA.module == "bg3.exe",
          "the writing instruction is not in the game executable", hitA)
    -- The host names the function with: gatekit decomp at <module>+0x<rva>

    -- (b) A position while walking: watch Translate.x, walk, see many writes from one instruction.
    local b = game.server([[
        local host = Osi.GetHostCharacter()
        GK.Memory.ClearWatch()
        local x, y, z = Osi.GetPosition(host)
        local f = GK.Memory.Field(host, "ls::TransformComponent", "Transform")
        local xa = f.Address + 16
        Osi.CharacterMoveToPosition(host, x + 12.0, y, z, "Run", "GKmemtest", 0)
        GK.Memory.Watch(xa, 4)
        return { x0 = x }
    ]])
    game.wait("server", "GK.Memory.Waiting() >= 3", 10)
    local hitB = game.server([[
        local hits = GK.Memory.Hits()
        GK.Memory.ClearWatch()
        local one = true
        for _, h in ipairs(hits) do if h.NextInstruction.RVA ~= hits[1].NextInstruction.RVA then one = false end end
        return { count = #hits, oneInstruction = one,
                 module = hits[1] and hits[1].NextInstruction.Module, rva = hits[1] and hits[1].NextInstruction.RVA }
    ]])
    check(hitB.count >= 3, "too few position writes were caught while walking", hitB)
    check(hitB.oneInstruction, "the position writes did not come from one instruction", hitB)

    -- (c) Who reads: a read watch on the standing host's position on the server (read by the
    -- systems every tick, written by none), and one on the client's own copy of it (read by the
    -- renderer), each owned by its side and listed apart.
    local r = game.server([[
        local host = Osi.GetHostCharacter()
        local f = GK.Memory.Field(host, "ls::TransformComponent", "Transform")
        return { slot = GK.Memory.Watch(f.Address + 16, 4, true) }
    ]])
    local rc = game.client([[
        local f = GK.Memory.Field(GK.Controlled()[1], "ls::TransformComponent", "Transform")
        return { slot = GK.Memory.Watch(f.Address + 16, 4, true), mine = #GK.Memory.Watches(), all = #GK.Memory.Watches(true) }
    ]])
    check(r.slot ~= nil and rc.slot ~= nil and rc.slot ~= r.slot, "a read watch on each side, in slots of their own", { server = r, client = rc })
    check(rc.mine == 1 and rc.all == 2, "each side lists its own watch; both are listed with all", rc)
    game.wait("server", "GK.Memory.Waiting() >= 1", 10)
    game.wait("client", "GK.Memory.Waiting() >= 1", 10)
    local readHit = [[
        local h
        for _, hit in ipairs(GK.Memory.Hits()) do if not hit.Changed then h = hit break end end
        GK.Memory.ClearWatch()
        return { changed = h and h.Changed, owner = h and h.Owner, left = #GK.Memory.Watches(true),
                 at = h and (h.NextInstruction.Module .. "+" .. string.format("0x%X", h.NextInstruction.RVA)) }
    ]]
    local reads = game.server(readHit)
    local readsClient = game.client(readHit)
    print("read by " .. tostring(reads.at) .. " (server), " .. tostring(readsClient.at) .. " (client)")
    check(reads.owner == "Server" and reads.changed == false and reads.left == 1, "the server's read hit, and its clear leaves the client's watch", reads)
    check(readsClient.owner == "Client" and readsClient.changed == false and readsClient.left == 0, "the client's read hit", readsClient)

    -- Freeze the hit points: damage no longer lowers the field; a clean removes the freeze.
    local fz = game.server([[
        local host = Mods.GateKit._mtest.host
        GK.Memory.Unfreeze()
        local f = GK.Memory.Field(host, "eoc::HealthComponent", "Hp")
        local hp0 = GK.Memory.Read(f.Address, "int32")
        GK.Memory.Freeze(f.Address, "int32", hp0)
        GK.Stage.Damage(7, host, "Bludgeoning", true)
        Mods.GateKit._mtest.hp0 = hp0
        return { hp0 = hp0, frozen = #GK.Memory.Frozen() }
    ]])
    check(fz.frozen == 1, "the freeze was not registered", fz)
    game.wait("server", "true", 1)   -- let a tick rewrite the frozen value
    local held = game.server([[
        local host = Mods.GateKit._mtest.host
        local f = GK.Memory.Field(host, "eoc::HealthComponent", "Hp")
        local held = GK.Memory.Read(f.Address, "int32")
        GK.Stage.Clean()
        return { held = held, frozenAfterClean = #GK.Memory.Frozen() }
    ]])
    check(held.held == fz.hp0, "the frozen hit points were lowered by damage", { held = held, fz = fz })
    check(held.frozenAfterClean == 0, "a clean did not remove the freeze", held)
end
