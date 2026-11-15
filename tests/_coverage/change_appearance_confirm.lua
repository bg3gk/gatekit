-- eoc::notification::CharacterChangedAppearanceComponent: the shortest way to make it appear again.
-- Claim: Confirming a Change Appearance screen raises eoc::notification::CharacterChangedAppearanceComponent for one frame on both sides, together with eoc::CustomIconComponent, esv::character_creation::ChangeAppearanceCompletedOneFrameComponent and AppearanceVisualTagComponent. Opening the screen lights ecl/eoc ChangeAppearanceDefinitionComponent, eoc definition::ChangeAppearanceComponent, ecl RandomAppearances/RandomCharacters/AppearanceCache. Field layouts NOT worked out.
-- Backed by: sighting only, reached by cheat, in TUT_Avernus_C
-- Still unresolved: Only one clean run: a second StartChangeAppearance (and StartRespec) on the same character opened no screen in the same session, so the sequence was not repeated. Field meaning unknown; no probe done.
-- Recorded 2026-11-13.
-- unreplayed since rewrite (2026-11-14): rewritten as a test from the free-form recipe's own steps, not yet replayed live.
-- Replay: gatekit --instance <copy> test tests/_coverage -k change_appearance_confirm

test { level = "GustavX:TUT_Avernus_C" }

function run(game)
    game.wait_state("Running", "server", 120)
    -- Watched from before the first step, so a component that lives one frame is seen too.
    watch("server", "eoc::notification::CharacterChangedAppearanceComponent")

    -- the mirror flow via the story: opens the CharacterChangeAppearance widget on the client after about 3 s
    game.lua("server", [[
local c = GK.Party()[1]
Osi.StartChangeAppearance(c.uuid)
]])

    game.wait("client", [[
GK.Gui.IsOpen("CharacterChangeAppearance")
]], 30)

    game.lua("client", [[
GK.Gui.Click("CharacterChangeAppearance", "randomNew")
]])
    game.sleep(1)

    -- the component lives one frame on both sides when confirmButton (FinishHenchmenCreating) runs
    game.lua("client", [[
GK.Gui.Click("CharacterChangeAppearance", "confirmButton")
]])
    game.sleep(2)

    local seen = alive("server", "eoc::notification::CharacterChangedAppearanceComponent", 10)
    print("eoc::notification::CharacterChangedAppearanceComponent alive on server: " .. fmt(seen))
end
