-- eoc::character_creation::LevelUpDefinitionComponent: the shortest way to make it appear again.
-- Claim: Opening the CharacterLevelUp widget lights ecl::character_creation::LevelUpDefinitionComponent, eoc::character_creation::LevelUpDefinitionComponent, eoc::character_creation::definition::LevelUpComponent, ecl::character_creation::AccumulatedChangesComponent, ecl::character_creation::PlayerCameraChangeDelayComponent. The character stayed level 1, so whether the XP was needed is unknown.
-- Backed by: sighting only, reached by cheat, in TUT_Avernus_C
-- Still unresolved: Which of the XP grant or merely the Show call is needed was not separated. Layouts untouched.
-- Recorded 2026-11-13.

-- stage: give XP then open the level-up window
local c = GK.Party()[1]
Osi.AddExplorationExperience(c.uuid, 5000)  -- server
-- ui: GK.Gui.Show("levelup")  -- the widget CharacterLevelUp appears late (seconds); GK.Gui.IsOpen("levelup") may be false at first; it appeared only after the GameMenu was closed
