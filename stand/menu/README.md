# The stand in the main menu

The main menu is a level drawn under the UI, not a recorded picture. With a profile that has
reached the third act it is `SYS_Menuscreen_Act3_B` (the game also ships `SYS_Menuscreen_Act1_A`).
Only the client runs there: the game state is `Menu`, there is no server and no `Osi`.

- **What plays.** Timelines of the dialogs `MainMenu/MM_*`: `MM_Open_Boats` behind the splash
  screen (the surface, the ships), `MM_Accept` for the descent (a camera track and a
  `TLSwitchLocationEvent`), `MM_Accept_Temple` under the menu itself (the four adventurers on the
  stairs, three rats, the torches). `gatekit data timeline MM_Accept_Temple` lists the actors.
- **The figures** are timeline dummies: client entities with `TLPreviewDummy`,
  `TimelineActorData`, `ClientTimelineActorControl`, `Visual`, `AnimationBlueprint` and
  `Level.LevelName` = the menu level. `TLPreviewDummy.OriginalCharacterTemplate` names each.
- **The camera** is a timeline camera: the one `Camera` entity that carries `SoundListener`
  (and `HiZ`). Its `Transform` can be written and the view follows.
- **Moving a figure.** The timeline's transform track writes a dummy's `Transform` every frame.
  A dummy whose `ClientTimelineActorControl.Timeline` is the null guid is left alone by it and
  keeps its animation; its picture moves with `entity.Visual.Visual:SetWorldTranslate{x,y,z}` and
  `:SetWorldRotate{x,y,z,w}`. Writing `Transform` alone moves nothing on the screen.

## dancer - an adventurer dances to the right of the menu

    ./gatekit --instance <copy> exec client "$(cat stand/menu/dancer.lua)"       # arm
    ./gatekit --instance <copy> exec client "$(cat stand/menu/dancer_off.lua)"   # disarm

Client context, armed in the main menu (`gatekit --instance <copy> up --main-menu`). One of the
menu's own four adventurers is taken off the timeline and put before the camera, to the picture's
right of the menu, where it sways and hops; the other three stay on the stairs. The place is
worked out from the camera's own view matrix, so it holds for another window shape. It fires when
the main menu is shown (`GK.Gui.Stuck() == "mainmenu"`) and the camera has stood still for half a
second; armed in a menu that already stands, that is half a second after arming. State:
`GKScene.menu_dancer = { armed, fired, waiting, error, log, spot, yaw }`. Options before arming:
`GKSceneOptions = { menu_dancer = { who, ahead, right, down, facing, sway, period, hop, still } }`.
The off script gives the adventurer back to the timeline, at the place it was taken from.

The dance is the scene's own turning and hopping of the figure over the idle animation it had: no
dance animation is played.

## Not done

- **Someone behind the statue on the splash screen.** `up --main-menu` goes through the splash
  screen by itself the moment it appears, and nothing found leads back to it (the UI event
  `OpenSplashScreen` raised in the menu changes nothing). A scene for the splash needs a start
  that stops there. The adventurers' dummies belong to `MM_Accept_Temple`; whether they exist
  while `MM_Open_Boats` plays is unseen.
- **A figure of the stand's own cast** (Raphael, Mizora). There is no server to spawn one, and
  the extender offers no client-side visual to load a character's look into; the scene borrows a
  figure the menu already has.
- **The Act 1 menu level** is not looked at: the dummies' templates in the scene are those of
  `SYS_Menuscreen_Act3_B`, and the scene says so and does nothing in another level.
