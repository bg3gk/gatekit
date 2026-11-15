-- eoc::progression::FeatComponent: the shortest way to make it appear again.
-- Claim: Level-up through the game's own CharacterLevelUp window, Astarion rogue 1 to 4 (Thief at 3; at 4 the feat tab: Ability Improvement, +2 Dexterity): eoc::progression::FeatComponent (server, 128 bytes) and ReplicatedFeatComponent (client+server, 24 bytes) came alive at the level-4 finish. The stall at level 3 was the subclass tab: finish stays disabled until a subclass is selected (Arcane Trickster needs spell picks; Thief needs none). At level 4 finish is disabled until two ability points are given via the VMAbility rightBtn.
-- Backed by: repeated: two runs from reset, each in a fresh capture, both lit FeatComponent (server) and ReplicatedFeatComponent (client and server) at the level-4 finish; the steps of this recipe are exactly the second run. verify: no layout in the build, nothing comparable, reached by play (XP by cheat, then the game's own window), in GustavX:SCL_Main_A
-- Still unresolved: gatekit test replay not run; FinishLevelUp via GK.Cheat.Run works where Click on finishLevelUpButton did not apply; a Finish with no subclass chosen puts the window in 'Finishing' with no controls - close and reopen
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k progression_feat_level4_window

test { level = "GustavX:SCL_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "eoc::progression::FeatComponent")

    game.lua("server", [[
Osi.AddExplorationExperience(Osi.GetHostCharacter(), 7000)
]])

    game.sleep(3)

    game.lua("client", [[
GK.Gui.Show('levelup')
]])

    game.sleep(6)

    game.lua("client", [[
GK.Cheat.Run('CharacterLevelUp','FinishLevelUp')
]])

    game.sleep(6)

    game.lua("client", [[
GK.Gui.Click('CharacterLevelUp','subClassTab')
]])

    game.sleep(2)

    game.lua("client", [[
GK.Gui.Click('CharacterLevelUp',{data='SelectableClass'},4)
]])

    game.sleep(2)

    game.lua("client", [[
GK.Cheat.Run('CharacterLevelUp','FinishLevelUp')
]])

    game.sleep(6)

    game.lua("client", [[
GK.Gui.Click('CharacterLevelUp','featTab')
]])

    game.sleep(2)

    game.lua("client", [[
GK.Gui.Click('CharacterLevelUp',{data='SelectableFeat'},1)
]])

    game.sleep(2)

    game.lua("client", [[
GK.Gui.Click('CharacterLevelUp',{name='rightBtn',data='VMAbility'},2)
]])

    game.lua("client", [[
GK.Gui.Click('CharacterLevelUp',{name='rightBtn',data='VMAbility'},2)
]])

    game.sleep(2)

    game.lua("client", [[
GK.Cheat.Run('CharacterLevelUp','FinishLevelUp')
]])

    game.sleep(5)

    local seen = alive("server", "eoc::progression::FeatComponent", 30)
    print("eoc::progression::FeatComponent alive on server: " .. fmt(seen))
end
