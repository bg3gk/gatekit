-- esv: the shortest way to make it appear again.
-- Claim: Pressing the alchemy page's CreateExtracts command (CharacterPanel, the herbs-into-extracts button) with herbs in the inventory makes esv::combine::AddToInventoryRequestOneFrameComponent live on the server for a frame, together with eoc::notification::CombineResultCreated and eoc::analytics::EventItemCombine on both sides. Alchemy extraction is an esv::combine path; the plain inventory item-on-item Combine was not reached.
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Still unresolved: ["Herbs were given with GK.Give (cheat); a player would pick them up. Fields not read: one-frame, size 0 in the ledger. UnlockRecipe fired again on the second run, so what it keys on (new recipe vs each combine) is unknown. Not seen: TransformedEntities, DyeRequest."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k combine_AddToInventoryRequestOneFrame

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::combine::AddToInventoryRequestOneFrameComponent")

    game.lua("server", [[
local h=Osi.GetHostCharacter(); for _,t in ipairs({'b3baa987-efe1-4cec-b290-74408d6d4771','16c583b8-4729-4603-aeee-d224bf645653','cfdb70fc-7fc1-4f18-9317-79704886e4d9'}) do GK.Give(t,4,h) end
]])

    game.sleep(3)

    game.lua("client", [[
GK.Cheat.Run('ModalTutorial','FinishTutorial')
]])

    game.key("i")

    game.sleep(2)

    game.lua("client", [[
GK.Gui.Page('CharacterPanel','ToggleAlchemy')
]])

    game.sleep(2)

    game.lua("client", [[
GK.Cheat.Run('CharacterPanel','CreateExtracts')
]])

    game.sleep(1)

    local seen = alive("server", "esv::combine::AddToInventoryRequestOneFrameComponent", 10)
    print("esv::combine::AddToInventoryRequestOneFrameComponent alive on server: " .. fmt(seen))
end
