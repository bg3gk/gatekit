-- ecl::dummy::DummyComponent: the shortest way to make it appear again.
-- Claim: Opening Photo Mode through the game command TogglePhotoModeOnCommand (TogglePhotoModeCommand ran twice and did nothing) creates the ecl::dummy::* set (Dummy, HasDummy, AnimationState, Unsheath, Splatter, EquipmentVisualsState, OriginalTransform, FootIKState, AvailableAnimations, Loaded, CharacterVFXInitialization, VFXEntities, MultiEffectCreateRequestOneFrame), eoc::photo_mode::*, ls::photo_mode::*, ecl::camera::PhotoModeCamera*, ecl::photo_mode::Camera*. Exit not found: TogglePhotoModeCommand/Esc/p did nothing; types stayed alive until bg3_reset.
-- Backed by: repeated, reached by play, in GustavX:WLD_Main_A
-- Still unresolved: ["verify: ecl::dummy / ecl::photo_mode / eoc::photo_mode prefixes: no layout in the build matches; ls::photo_mode agreed 6 disagreed 1 (ColorGradingComponent.Contrast at \u002B0x4, extender: property does not exist); ecl::camera nothing comparable.","StoredClothComponent not alive. No in-UI picture taken (TakePhoto/RequestPhoto ran, no visible effect). Held camera keys w/a/d/e: probe_watch of PhotoModeCameraInputComponent nothing moved. UpdateFoV etc need a light C\u002B\u002B object parameter that GK.Command cannot give."]
-- Recorded 2026-11-14. Replay: gatekit --instance <copy> test tests/_coverage -k photo_mode_dummy

test { level = "GustavX:WLD_Main_A" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("client", "ecl::dummy::DummyComponent")

    game.lua("ui", [[
GK.Command("TogglePhotoModeOnCommand")
]])
    game.sleep(3)

    local seen = alive("client", "ecl::dummy::DummyComponent", 10)
    print("ecl::dummy::DummyComponent alive on client: " .. fmt(seen))
end
