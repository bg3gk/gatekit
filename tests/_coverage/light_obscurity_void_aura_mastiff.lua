-- esv::light::GameplayObscurityComponent: the shortest way to make it appear again.
-- Claim: Status VOID_AURA alone (not the shadowmastiff polymorph) puts esv::light::GameplayObscurityComponent on the host (float 6 at +0). HAV_DevilishOX_POLYMORPH_SHADOWMASTIFF alone, from a reset: did NOT light it (isolated, one at a time, fresh capture each).
-- Backed by: repeated and isolated: VOID_AURA lit it in 2 fresh captures from reset, mastiff alone 0 times; verify on server agreed 1, disagreed 0, reached by cheat, in GustavX:SCL_Main_A
-- Still unresolved: Shadow curse proper not played; recipe name still says mastiff but needs only VOID_AURA
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k light_obscurity_void_aura_mastiff

test { level = "GustavX:SCL_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::light::GameplayObscurityComponent")

    game.lua("server", [[
Osi.ApplyStatus(Osi.GetHostCharacter(),"VOID_AURA",-1)
]])

    game.sleep(4)

    local seen = alive("server", "esv::light::GameplayObscurityComponent", 10)
    print("esv::light::GameplayObscurityComponent alive on server: " .. fmt(seen))
end
