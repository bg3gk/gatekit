# The stand in character creation

Character creation is the level `SYS_CC_I`, with a server: the stage verbs work there as in any
level. The character on the screen is a client-only dummy (`ClientCCDummy`), the entity named by
`ClientCCDummyDefinition.Dummy` of the definition that carries `CCCharacterDefinition`; it stands
at `9.99, 0.89, 111.14` and the creation camera looks at it from `7.43, 1.94, 113.54`. The server's
four player characters stand elsewhere in the level and are not drawn in this view.

A character can be created only on the dummy's own platform, up to about 1.6 m behind it
(`Osi.CreateAt` answers nothing further out); an item anywhere. A character created on the
platform can be moved off it (`Osi.TeleportToPosition`, or a seat taken with `Osi.Use`).

## The driver's way: splash, menu, creation, the prologue

Each step is one call; the driver reads the answer before the next. `U` is
`./gatekit --instance <copy> exec ui`, `C` is `exec client`, `S` is `exec server`; `W` is the
widget `'CharacterCreation'`. The window is driven by the general verbs of `GK.Gui`
(docs/contracts/lua-api.md, "Look"): `GK.Gui.Look(W)` lists what the page shows with the handles
the other verbs take, and nothing below is code written for this window.

| Step | Call | Wait for |
|---|---|---|
| the splash screen | `gatekit --instance <copy> up --splash`; `U "return GK.Gui.Stuck()"` answers `splash` | - |
| press any key | `U "return GK.Cheat.Recover()"` (the page's own event `OpenMainMenu`) | `Stuck()` answers `mainmenu` at once; the camera's descent is over when the `Camera` entity that carries `SoundListener` stands still (`stand/menu/dancer.lua` waits so) |
| New Game | `U "return GK.Gui.NewGame()"` | `ok`; the new-game settings are open |
| confirm | `U "return GK.Gui.NewGame()"` again | the client leaves the menu (`StartLoading`) |
| the film of a new game | `C "return GK.Gui.Stuck()"` until it answers `film`, then `U "return GK.Cheat.Recover()"` (Escape) | client and server `Running` in `SYS_CC_I`; `Stuck()` answers nothing |
| origin | `GK.Gui.Select(W,'presetOrigins','<name>')` (`customOrigins` for a character of one's own) | `ok`, `reads` = the number chosen |
| a tab of the choices | `GK.Gui.Select(W,'gameplayTabs','raceTab')` (`originTab`, `subRaceTab`, `classTab`, `backgroundTab`, `abilityTab`; the tabs under a tab are the lists `subTabsSpellsList`, `subTabsPassivesList`, `subTabsEquipmentList`) | `ok` |
| race, subrace, class, background | `GK.Gui.Select(W,'raceListBox','<Guid or name>')`; the other lists by the handle `Look` gives on their tab (`classList`) | `ok`; the server's `CCCharacterDefinition.Definition` reads it |
| abilities | `GK.Gui.Click(W,'AddAbility[n]')`, `'ReduceAbility[n]'` (the n-th ability in the window's order; `Look(W,{detail=true})` names each); the +2 and +1 rows are the buttons `button[n]`; `Recommend`, `Reset` | `saw` names `UsedAbilityPoints`; a point that is not there is refused (`disabled`) |
| cantrips, spells | `GK.Gui.Click(W,'subTabsSpellsList[2]#1')`, then `'SelectAvailableSpell[n]'`, `'DeselectAddedSpell[n]'` | `saw` |
| body type | `GK.Gui.Click(W,'femaleBtn')` (`maleBtn`, `femaleStrongBtn`, `maleStrongBtn`) | `saw` names the change |
| the appearance panel | `GK.Gui.Click(W,'EditAppearanceButton')`, then a tab: `GK.Gui.Select(W,'appearanceTabs','generalTab')` (`tattooAndPiercingTab`, `eyesTab`, `makeupTab`, `hairTab`, `facialHairTab`, `hornsTab`, `draconicTab`, `tailTab`: those the race has) | `saw = "the controls shown changed"` |
| a part of the look | `GK.Gui.Select(W,'SelectableHeadsListBox', n)` and the other lists of the tab (`SelectableHairListBox`, `SelectablePiercingsListBox`, `SelectableMakeupListBox`, `SelectableTailsListBox`, `SelectableScarsListBox`, `SelectableVoicesListBox`), by number or by the name shown | `ok`, `reads` |
| a colour | the lists behind the swatch buttons, by their `~` handles (`Look(W,{all=true,under='<panel>'})`): `~generalPanel/ListBox[2]` (skin), `~eyecolourListBox`, `~SelectableHairColorsListBox`, `~SelectableHairHighlightColorsListBox`, `~SelectableHairGreyingColorsListBox`, `~SelectableTattooColorsListBox`, `~SelectableMakeupColorsListBox`, `~SelectableLipTintColorsListBox` | `ok`, `reads` |
| a slider | `GK.Gui.Set(W,'Age/Slider', 40)` (0 to 100; `Freckles/Slider`, `FrecklesIntensity/Slider`, `Vitiligo/Slider`) | `ok`, `after` |
| back, on | `GK.Gui.Click(W,'backToGameplayPage')`; `GK.Gui.Click(W,'GameplayProceedButton')` or `'toNamingPage'` (refused while a choice is unfinished) | `saw = "the controls shown changed"` |
| the name | `GK.Gui.Set(W,'characterName','<name>')` | `ok`; the server's definition reads the name |
| the guardian | `GK.Gui.Click(W,'namingToCompanion')`; its page is the same panel: `appearanceTabs` (its race is the list `raceTabPanel/ListBox`), the same lists, swatches and sliders | the server's `CCCompanionDefinition` reads each choice |
| finish | `GK.Gui.Click(W,'ventureForth')` | `ok` (`IsLocked` turns true) |
| the opening film | `C "return GK.Gui.Stuck()"` answers `film` while the client sits in `StartLoading`; `U "return GK.Cheat.Recover()"` | client and server `Running`, the host's region `TUT_Avernus_C` |

A choice in the window answers within a tenth of a second and the server's definition follows
within a second.

The items of the look (heads, hair, colours) carry no Guid in the window: a list's own selection
is what the game takes, so they are chosen by number or by the name the window shows. A saved
character's look is Guids, and nothing here maps a Guid to an item of a list: the saved look is
applied by `appearance.lua`, past the window, as the next section says.

A swatch button opens its colours through its `IsChecked`, which the extender cannot set: a colour
is chosen in the swatch's list while the swatch panel stays closed on the screen.

## The saved character's way: the look written past the window

The same way with the steps that differ when a saved look is applied by `appearance.lua`,
`choose.lua` and `page.lua`.

| Step | Call | Wait for |
|---|---|---|
| New Game | `U "return GK.Gui.NewGame()"` | `ok`; the new-game settings are open |
| confirm | `U "return GK.Gui.NewGame()"` again | the client leaves the menu (`StartLoading`) |
| the film of a new game | `C "return GK.Gui.Stuck()"` until it answers `film`, then `U "return GK.Cheat.Recover()"` (Escape) | client and server `Running` in `SYS_CC_I`; `Stuck()` answers nothing |
| origin | `U "return GK.Gui.Select('CharacterCreation','presetOrigins',7)"` (the Dark Urge is the seventh) | `Data('CharacterCreation').SelectedBackground.Guid` |
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
`SelectedIndex` of the list `flowPage` (a chain in `Public/Game/GUI/Library/CCLib_k.xaml`, which
`GK.Gui.Click` runs), as `page.lua` does directly. `ventureForth` runs the window's
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
`ClientCCCompanionDefinition`. A choice in a list of the guardian's page sends its definition; a
look written past the window by `appearance.lua` is not sent by itself, so `appearance_server.lua`
writes the server's copy as well - a cheat, the server checks nothing. The guardian in the prologue
then carries the look.

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
