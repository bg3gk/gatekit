# Leads that are not recipes yet

Sightings whose steps are not unambiguous enough to be written as a replayable recipe in
`scenarios/coverage`. Their lines in `findings.jsonl` say `moved_to` this file. Each says what is
open and what would confirm it. The leads for types never seen alive are `leads.jsonl`
(`gatekit leads`); suspected layouts of Script Extender's are in
[../open-items.md](../open-items.md).

## `levelup_window`: `eoc::character_creation::LevelUpDefinitionComponent` (TUT_Avernus_C, cheat, sighting)

Opening the CharacterLevelUp widget lit ecl/eoc `character_creation::LevelUpDefinitionComponent`,
`eoc::character_creation::definition::LevelUpComponent`,
`ecl::character_creation::AccumulatedChangesComponent` and `PlayerCameraChangeDelayComponent`.
The character stayed level 1, so whether the experience was needed is unknown. The steps were: on
the server `Osi.AddExplorationExperience(GK.Party()[1].uuid, 5000)`; in the UI
`GK.Gui.Show("levelup")`. Not replayable as written: the widget appeared seconds late,
`GK.Gui.IsOpen("levelup")` was false at first, and it appeared only after the GameMenu was closed,
by a step that is not named. Not separated: the experience grant against the Show call alone. To
confirm: from a fresh game, watch the type, show the window without experience, then with it,
closing the GameMenu explicitly. (`progression_feat_level4_window` reaches the level-up window
by the game's own path.)

## `cc_body_type_visual_changed`: `ecl::character_creation::LoadedBodyTypeVisualChangedComponent` (SYS_CC_I, play, sighting)

Clicking the body-type radio buttons of the CharacterCreation widget through `GK.Gui.Click`
(SetBodyTypeAndShape, then EditAppearanceButton -> SetCameraMode) coincided with this one-frame
component in the client capture; not isolated to one click, and a second round of
femaleBtn/maleBtn clicks did not show it again. The clicks recorded were
`GK.Gui.Click(CharacterCreation,raceTab)`, `(CharacterCreation,maleStrongBtn)`,
`(CharacterCreation,EditAppearanceButton)` - unquoted names, and other buttons than the claim
names. Not replayable: which click creates it is the open question, and the start (character
creation from the main menu) is not given. To confirm: in character creation, watch the type on
the client and click one button at a time.

## `photo_mode_enter`: `eoc::photo_mode::SessionComponent` (WLD_Main_A, play, sighting)

The f9 key enters photo mode (one SessionComponent, byte +0x0 = 0x01); `photo_mode_f9` replays
that. `GK.Command("TogglePhotoModeCommand")` returned true and produced no SessionComponent within
4 s and a re-read, while a Dialogue widget was open (`GK.Blocking()` dialog=true), which may gate
it. `gui::DCPhotoMode` has TrackCharacter, FieldOfView and Brightness; clicking
PART_IncreaseRepeatButton moved neither FoVComponent's bytes nor FieldOfView (no property setter
found); DummyShowSplatter, CameraTracking and DummyEquipmentSetupOneFrame were not lit; ActorSettings
was empty, so no dummy was listed. To confirm the command path: run it with no dialogue open,
watching SessionComponent.
