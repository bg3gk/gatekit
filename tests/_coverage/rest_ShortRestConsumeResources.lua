-- esv::rest::ShortRestConsumeResourcesComponent: the shortest way to make it appear again.
-- Claim: From a clean checkpoint in WLD_Main_A, the host damaged with Osi.ApplyDamage and then GK.Command("ShortRest") on the client lights esv::rest::ShortRestConsumeResourcesComponent (and ShortRestResultEventOneFrame); the host heals. Seen twice (second in a fresh capture). Short rests have two charges.
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Still unresolved: ["Fields not read (one-frame). A short rest with a full-health host not tried. Charges: two used, third not tried."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k rest_ShortRestConsumeResources

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::rest::ShortRestConsumeResourcesComponent")

    game.lua("server", [[
Osi.ApplyDamage(Osi.GetHostCharacter(), 4, "Bludgeoning")
]])

    game.sleep(2)

    game.lua("client", [[
GK.Command("ShortRest")
]])

    game.sleep(4)

    local seen = alive("server", "esv::rest::ShortRestConsumeResourcesComponent", 10)
    print("esv::rest::ShortRestConsumeResourcesComponent alive on server: " .. fmt(seen))
end
