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

## A figure of the scene's own

`Ext.ClientVisual.CreateDummy(template, position, rotation)` of GateKit's Script Extender makes a
new figure on the client, with the functions the game's timeline system makes its own dummies
with (`code:ecl::TLPreviewDummyHelpers::CreateDummy`, `code:ecl::TLPreviewDummyHelpers::SetupDummy`,
the template overload of `PrePrepareDummy`, `code:ecl::TLPreviewDummyHelpers::LoadDummy`,
`code:ecl::TLPreviewDummyHelpers::AttachDummyToScene`); `Ext.ClientVisual.DestroyDummy(entity)`
removes it. It needs a character root template of the global bank
(`Ext.Template.GetAllRootTemplates()` lists them; a character whose look is on a placed object of
a level, such as Raphael's human form, has none) and a level on the client, which the menu and the
splash screen have. The new figure is drawn on the splash screen too, where the menu's own parked
dummies are not. It stands in its animation blueprint's pose; no timeline plays an animation on
it. `Ext.Entity.GetAllEntitiesWithComponent("TLPreviewDummy")` does not list it: a scene keeps the
entity it was given.

**Light.** The place before the menu's camera is dark. A light of the level that carries
`LightMovement` (the torches' flickering lights) is put where its `Transform` says every frame,
so writing its `Transform` moves it; a light without `LightMovement` ignores both its `Transform`
and `Light.PositionOffset`. The scene borrows one torch's light and puts it back.

## dancer_new - a new figure dances to the right of the menu, lit

    ./gatekit --instance <copy> exec client "$(cat stand/menu/dancer_new.lua)"       # arm
    ./gatekit --instance <copy> exec client "$(cat stand/menu/dancer_new_off.lua)"   # disarm

The dancer as above, with a figure of its own (Vlaakith by default: `who` takes any character
root template) and the four adventurers left on the stairs. The light `213b0683-...` of
`SYS_Menuscreen_Act3_B` (a torch's point light, radius 6) is put between the figure and the
camera at chest height (`light`, `light_toward`, `light_up`; `light = false` leaves the figure
dark). State: `GKScene.menu_dancer_new`. The off script removes the figure and puts the light
back; so does leaving the menu.

## statue - someone behind the statue on the splash screen

    ./gatekit --instance <copy> exec client "$(cat stand/menu/statue.lua)"       # arm
    ./gatekit --instance <copy> exec client "$(cat stand/menu/statue_off.lua)"   # disarm

Client context, armed while the splash screen stands (`gatekit --instance <copy> up --splash`;
the widget `SplashScreen`, passed by `GK.Cheat.Recover()`). The camera stands at
`984.03, 1.98, -1075.00`; the statue is a scenery object 4.7 m ahead of it and 2.2 m to the
picture's right. A new figure stands behind it (`ahead`, `right`, `down` from the camera) and
leans out to the picture's left once in `period` seconds by `lean` metres. The figure is removed
the moment the splash screen is passed. State: `GKScene.menu_statue`.

## Not done

- **The menu's own parked dummies on the splash screen** stay undrawn: each has
  `TLPreviewDummy.ActiveDummy = false`, `ClientInvisibilityVisual.State = 1` and a visual whose
  `CullFlags` lack `CullRenderView1`, and writing these by hand does not draw one. The scenes
  make a figure of their own instead.
- **The Act 1 menu level** is not looked at: the dummies' templates in the scene are those of
  `SYS_Menuscreen_Act3_B`, and the scene says so and does nothing in another level.
