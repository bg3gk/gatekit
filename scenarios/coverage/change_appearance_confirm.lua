-- eoc::notification::CharacterChangedAppearanceComponent: the shortest way to make it appear again.
-- Claim: Confirming a Change Appearance screen raises eoc::notification::CharacterChangedAppearanceComponent for one frame on both sides, together with eoc::CustomIconComponent, esv::character_creation::ChangeAppearanceCompletedOneFrameComponent and AppearanceVisualTagComponent. Opening the screen lights ecl/eoc ChangeAppearanceDefinitionComponent, eoc definition::ChangeAppearanceComponent, ecl RandomAppearances/RandomCharacters/AppearanceCache. Field layouts NOT worked out.
-- Backed by: sighting only, reached by cheat, in TUT_Avernus_C
-- Still unresolved: Only one clean run: a second StartChangeAppearance (and StartRespec) on the same character opened no screen in the same session, so the sequence was not repeated. Field meaning unknown; no probe done.
-- Recorded 2026-11-13.

-- stage: the mirror flow via the story
local c = GK.Party()[1]
Osi.StartChangeAppearance(c.uuid)   -- server; opens the CharacterChangeAppearance widget on the client after ~3 s
-- client/ui, once GK.Gui.IsOpen("CharacterChangeAppearance"):
-- GK.Gui.Click("CharacterChangeAppearance", "randomNew"); GK.Gui.Click("CharacterChangeAppearance", "confirmButton")
-- the component lives one frame on both sides when confirmButton (FinishHenchmenCreating) runs; bg3_seen_mark before the click
