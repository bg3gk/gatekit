# The stand in character creation

Character creation is the level `SYS_CC_I`, with a server: the stage verbs work there as in any
level. The character on the screen is a client-only dummy (`ClientCCDummy`), the entity named by
`ClientCCDummyDefinition.Dummy` of the definition that carries `CCCharacterDefinition`; it stands
at `9.99, 0.89, 111.14` and the creation camera looks at it from `7.43, 1.94, 113.54`. The server's
four player characters stand elsewhere in the level and are not drawn in this view.

A character can be created only on the dummy's own platform, up to about 1.6 m behind it
(`Osi.CreateAt` answers nothing further out); an item anywhere. A character created on the
platform can be moved off it (`Osi.TeleportToPosition`, or a seat taken with `Osi.Use`).

## The driver's way: menu, creation, the saved character, the prologue

Each step is one call; the driver reads the answer before the next. `U` is
`./gatekit --instance <copy> exec ui`, `C` is `exec client`, `S` is `exec server`.

| Step | Call | Wait for |
|---|---|---|
| New Game | `U "return GK.Gui.NewGame()"` | `ok`; the new-game settings are open |
| confirm | `U "return GK.Gui.NewGame()"` again | the client leaves the menu (`StartLoading`) |
| the film of a new game | `C "return GK.Gui.Stuck()"` until it answers `film`, then `U "return GK.Cheat.Recover()"` (Escape) | client and server `Running` in `SYS_CC_I`; `Stuck()` answers nothing |
| origin | `U "return GK.Gui.Select('CharacterCreation','presetOrigins',7)"` (the Dark Urge is the seventh; the answer says `ok = false` though the origin is taken) | `Data('CharacterCreation').SelectedBackground.Guid` |
| race, subrace | `GK.Gui.Click('CharacterCreation','raceTab')`, then `choose.lua` with `from = "SelectableRaces"`; `subRaceTab` and `SelectableSubRaces` the same way | `ok` with `selected` = the Guid asked for |
| class | `Click(... 'classTab')`, `GK.Gui.Select('CharacterCreation','classList', n)` | `ok` |
| body type | `GK.Gui.Click('CharacterCreation','femaleBtn')` (`maleBtn`, `femaleStrongBtn`, `maleStrongBtn`) | `saw` names the change |
| the saved look | set `GKSceneOptions.appearance` in the client context, `C "$(cat stand/creation/appearance.lua)"` | `ok`; the dummy is redrawn within a few seconds |
| send it | `U "return GK.Cheat.Run('CharacterCreation','ChangeCharacterName','<name>')"` | the server's `CCCharacterDefinition.Definition` reads the look back |
| the name page, the guardian page | `page.lua` with `page = 3`, then `page = 2` | `ok` |
| the guardian (optional) | on its page: `raceTab`, `choose.lua`; `subRaceTab`, `choose.lua`; then `appearance.lua` with `who = "guardian"` in the client context and `appearance_server.lua` in the server context | both `ok` |
| finish | `U "return GK.Gui.Click('CharacterCreation','ventureForth')"` | `ok` (`IsLocked` turns true) |
| the opening film | `C "return GK.Gui.Stuck()"` answers `film` while the client sits in `StartLoading`; `U "return GK.Cheat.Recover()"` | client and server `Running`, the host's region `TUT_Avernus_C` |

The film of a new game appears under ten seconds after the confirmation and the creation screen
stands three seconds after it is skipped. The opening film appears two seconds after the finish;
the level does not load while it plays, and it plays for minutes: skipped, the prologue is
`Running` about fifteen seconds later, in its first dialogue.

The window's own buttons `GameplayProceedButton` and `namingToCompanion` run no command: they set
`SelectedIndex` of the list `flowPage`, which `page.lua` does. `ventureForth` runs the window's
command `FinishCreating` and is enabled when `IsCharacterComplete` (a name is set).

## The saved character

A saved character keeps its creation in two components, and the definition takes them field for
field:

| The saved character | The creation definition (`CCCharacterDefinition.Definition`) | Set by |
|---|---|---|
| `CharacterCreationStats.Race`, `.SubRace` | `.Definition.Race`, `.Definition.Subrace` | the window's lists (`choose.lua`) |
| `CharacterCreationStats.BodyType`, `.BodyShape` | `.Definition.BodyType`, `.Definition.BodyShape` | the body-type buttons |
| `GameObjectVisual.RootTemplateId` | `.Definition.RootTemplate` | follows race, subrace and body type |
| `Origin.field_18`, `Background.Background` | `.Definition.Origin`, `.Definition.Background` | the origin list (an origin with its own background sets it) |
| `Classes.Classes[1].ClassUUID` | `.LevelUpData.Class` | the class list; the game fills the class's choices itself |
| `CharacterCreationAppearance.Visuals`, `.Elements`, `.AdditionalChoices`, `.EyeColor`, `.SecondEyeColor`, `.HairColor`, `.SkinColor` | `.Visual`, the same names | `appearance.lua` |
| `CharacterCreationStats.Name` | `.Name` | `appearance.lua` and the name command |
| `CharacterCreationStats.PointBuyAbilities` | `.Abilities` | `appearance.lua` |
| `Voice.Voice` | `.Definition.Voice` | `appearance.lua` |

The client keeps the definition in three places: what it edits and sends
(`ClientCCBaseDefinitionState.Definition`), what the dummy is drawn from (`ClientCCDummyDefinition`;
its `ChangeId2` moved redraws the look, its `ChangeId` the body) and the looks remembered by root
template (`ClientCharacterCreationAppearanceCache`). `appearance.lua` writes all three. The server's
copy changes when the window next sends the definition: the client sends it whole, and the
window's own name command is such a change. The server takes the look, the name, the abilities and
the voice from that message; the character that lands in the prologue carries them in
`CharacterCreationAppearance` and `CharacterCreationStats`.

What does not work: writing the server's `CCCharacterDefinition` and replicating it changes the
client's replicated copy and neither the dummy nor the window; setting `SelectedRace` on the
window's view model is not taken (the list box's selection is).

The guardian is the definition with `CCCompanionDefinition`; the client edits
`ClientCCCompanionDefinition`. No change of the window was found that sends the guardian's look, so
`appearance_server.lua` writes the server's copy as well - a cheat, the server checks nothing. The
guardian in the prologue then carries the look.

## appearance, choose, page - the saved character's steps

    C "$(cat stand/creation/appearance.lua)"                                   # GKSceneOptions.appearance first
    S "$(cat stand/creation/appearance_server.lua)"                            # the guardian only
    U "GKSceneOptions = { choose = { from = 'SelectableRaces', guid = '...' } } $(cat stand/creation/choose.lua)"
    U "GKSceneOptions = { page = 3 } $(cat stand/creation/page.lua)"

Each file's head says what it takes and answers.

## onlooker - Raphael plays lanceboard behind the character

    S "$(cat stand/creation/onlooker.lua)"       # arm
    S "$(cat stand/creation/onlooker_off.lua)"   # disarm

Server context. A small round table with a lanceboard and a stool stand behind the dummy, to the
picture's right; Raphael (his root transformed into the placed object `S_GLO_Monitor`, as the scene
`watchers` does) is created on the platform, takes the stool (`Osi.Use`) and plays the looping
animation `CUST_PlayLanceBoard_01`, as in Last Light. He is seated and playing about five seconds
after arming. A sitter faces against the stool's own facing. State and options: the file's head.

## stargazer - a tiefling at a telescope further behind

    S "$(cat stand/creation/stargazer.lua)"       # arm
    S "$(cat stand/creation/stargazer_off.lua)"   # disarm

Server context. The Emerald Grove's stargazer (the root template `Tieflings_Female_Nadira`) stands
at a telescope (`TOOL_GEN_Telescope_A`) behind the dummy, to the picture's left, and leans to the
eyepiece. The looking animation has no short name; `Osi.PlayLoopingAnimation` takes the animation
resources the timeline `BHVR_WRLD_DEN_TelescopeStargazing` plays. That timeline itself is accepted
by the server (`Osi.StartBehaviorDialog_Internal` with `NULL_00000000-...` for the absent speakers
and trigger answers success) and nothing of it is seen: no timeline actor appears on the client.

## Not done

- **The line over a head.** `GK.Say.Line` answers `ok` and nothing is drawn: the widget
  `OverheadInfo` is open and its `Visibility` is `Hidden` while the creation screen stands.
- **The telescope's own animation** is asked for and not seen to move.
- **The guardian's look by the client's own message.**
