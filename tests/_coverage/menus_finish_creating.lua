-- esv::character_creation::CreationFinishedOneFrameComponent: the shortest way to make it appear again.
-- Claim: Walking the Design-Your-Character pages with the real GUI and pressing FinishCreating (ventureForth) makes CreationFinishedOneFrame live on the server.
-- Backed by: sighting only, reached by play, in GustavX:SYS_CC_I
-- Still unresolved: ["The ability bonuses: on a fresh start (runner menus, 14 Nov day) buttons 1..3 toggled the bonuses OFF; control numbers 25 (+2 row) and 32 (+1 row) made IsCharacterCompleteExceptName true. Control numbers can move with the game's UI. After FinishCreating the client sat in StartLoading for 9+ min once. Not replayed."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k menus_finish_creating

test { level = "GustavX:SYS_CC_I" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "esv::character_creation::CreationFinishedOneFrameComponent")

    -- closes the tutorial box
    game.lua("client", [[
GK.Gui.Resolve()
]])
    game.sleep(1)

    game.lua("client", [[
GK.Gui.Click('CharacterCreation','classTab')
]])
    game.sleep(1)

    game.lua("client", [[
GK.Gui.Select('CharacterCreation','classList',1)
]])
    game.sleep(1)

    game.lua("client", [[
GK.Gui.Click('CharacterCreation','abilityTab')
]])
    game.sleep(1)

    game.lua("client", [[
GK.Gui.Click('CharacterCreation','Recommend')
]])
    game.sleep(1)

    game.lua("client", [[
GK.Gui.Click('CharacterCreation',25)
]])
    game.sleep(1)

    game.lua("client", [[
GK.Gui.Click('CharacterCreation',32)
]])
    game.sleep(1)

    game.lua("client", [[
GK.Gui.Click('CharacterCreation','GameplayProceedButton')
]])
    game.sleep(2)

    game.lua("client", [[
GK.Gui.Click('CharacterCreation','namingToCompanion')
]])
    game.sleep(2)

    game.lua("client", [[
GK.Gui.Click('CharacterCreation','ventureForth')
]])
    game.sleep(3)

    local seen = alive("server", "esv::character_creation::CreationFinishedOneFrameComponent", 10)
    print("esv::character_creation::CreationFinishedOneFrameComponent alive on server: " .. fmt(seen))
end
