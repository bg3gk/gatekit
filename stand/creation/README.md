# The stand in character creation

Character creation is the level `SYS_CC_I`, with a server: the stage verbs work there as in any
level. The character on the screen is a client-only dummy (`ClientCCDummy`), the entity named by
`ClientCCDummyDefinition.Dummy` of the definition that carries `CCCharacterDefinition`; it stands
at `9.99, 0.89, 111.14` and the creation camera looks at it from `7.43, 1.94, 113.54`. The server's
four player characters stand elsewhere in the level and are not drawn in this view.

## Getting there from the main menu

Each step is one call; the driver reads the answer before the next.

1. `exec ui "return GK.Gui.NewGame()"` - the main menu's New Game; the new-game settings open.
2. `exec ui "return GK.Gui.NewGame()"` again - the settings are confirmed; the client leaves the
   menu (`StartLoading`), the server starts.
3. The client stops in `PrepareRunning` behind the recorded film of a new game:
   `exec client "return GK.Gui.Stuck()"` answers `film`. `exec ui "return GK.Cheat.Recover()"`
   presses Escape, as a player would; the client is then `Running` in `SYS_CC_I`.

The load from the confirmation to the film takes under a minute (the server's Lua starts about
ten seconds after the confirmation); the film stays until it is skipped or ends.

## onlooker - Raphael beside the character being created

    ./gatekit --instance <copy> exec server "$(cat stand/creation/onlooker.lua)"       # arm
    ./gatekit --instance <copy> exec server "$(cat stand/creation/onlooker_off.lua)"   # disarm

Server context. Places Raphael (his root transformed into the placed object `S_GLO_Monitor`, as
the scene `watchers` does) to the picture's right of the dummy and a little behind it, then says
a line over his head (`GK.Say.Line`) 1.5 s later. State: `GKScene.onlooker = { armed, fired, log,
id, at, yaw }`. Options before arming: `GKSceneOptions = { onlooker = { side, behind, at, yaw,
line, template, look } }`.

## Not done

- **Facing.** The figure shows its back to the camera: the yaw given to `GK.Stage.Spawn` and to
  `Osi.ToTransform` does not hold after the transform into the look (`GK.Stage.Yaw` reads 0).
- **The line** is logged as said and is not seen on the creation screen.
- **An animation** of the figure: none is played.
- **The saved appearance** is not applied. The server keeps what is being created in the
  component `CCCharacterDefinition` (`Definition.Definition`: `Race`, `Subrace`, `BodyType`,
  `BodyShape`, `RootTemplate`, `Voice`, `Origin`, `Background`; `Definition.Visual`: `Elements`,
  `AdditionalChoices`, the colours; `Definition.Name`; `Definition.Abilities`); the saved
  character keeps the same things in `CharacterCreationStats` and `CharacterCreationAppearance`.
  Whether writing the component redraws the dummy and survives the confirmation is untried.
- **The way on to the prologue.** The pages are walked by `GK.Gui.Click('CharacterCreation', ...)`
  in `tests/_coverage/menus_finish_creating.lua` (`ventureForth` last); it is not replayed here.
