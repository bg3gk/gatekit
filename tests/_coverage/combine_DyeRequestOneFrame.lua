-- esv::combine::DyeRequestOneFrameComponent: the shortest way to make it appear again.
-- Claim: Dyeing an armour through the game's Combine window (use a dye, put an armour into the free slot, start) makes esv::combine::DyeRequestOneFrameComponent live on the server for a frame, together with eoc::item::DyeComponent, esv::unsheath::CombiningComponent and eoc::legacy::action::ASCombineItemComponent.
-- Backed by: repeated, reached by cheat, in GustavX:WLD_Main_A
-- Still unresolved: ["Fields not read: one-frame component. Dye and armour were given with GK.Give (cheat); recorded on a Russian client; the item names here are the English client's, not replayed yet, and the window names were the Russian client\u0027s. TransformedEntitiesOneFrameComponent not reached: ItemDo use opens the Combine window only for dyes; herbs, extracts, ingredients and arrows go into the alchemy pouch where ItemDo cannot find them, and alchemy page CraftItems with no recipe selected lit nothing."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k combine_DyeRequestOneFrame

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::combine::DyeRequestOneFrameComponent")

    game.lua("server", [[
local h=Osi.GetHostCharacter(); GK.Give('a94ac1cd-96c9-4775-8dcd-c2b581bfeb50',1,h); GK.Give('02ae5d88-8044-43df-8363-02a2900776db',1,h)
]])

    game.sleep(3)

    game.lua("client", [[
return GK.Gui.ItemDo('use','Light Blue Dye')
]])

    game.sleep(1)

    game.lua("client", [[
return GK.Gui.Combine{with='Leather Armour'}
]])

    game.sleep(1)

    game.lua("client", [[
return GK.Gui.Combine{start=true}
]])

    game.sleep(2)

    local seen = alive("server", "esv::combine::DyeRequestOneFrameComponent", 10)
    print("esv::combine::DyeRequestOneFrameComponent alive on server: " .. fmt(seen))
end
