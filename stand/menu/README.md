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

## The driver's order in the menu

The video's whole order is in [../scenes/README.md](../scenes/README.md). Its part here, `C` being
`./gatekit --instance <copy> exec client` and `U` `exec ui`:

| Step | Call | Wait for | Rough |
|---|---|---|---|
| the start | `gatekit --instance <copy> up --splash` | `U "return GK.Gui.Stuck()"` answers `splash` | - |
| the company, armed | `C "$(cat stand/menu/company.lua)"` | `GKScene.menu_company.fired`, no `waiting`: the figures stand and play their idles within two seconds | the places are set for this camera by eye |
| Orin, armed | `C "$(cat stand/menu/orin.lua)"` | nothing yet: it fires under the ground | - |
| press any key | `U "return GK.Cheat.Recover()"` | `GKScene.menu_company.done` at once; the camera's descent; `GKScene.menu_orin.stage` goes `loading` ... `gone`, `done` about twenty seconds after the key | Orin's beats are counted in seconds |
| on | `U "return GK.Gui.NewGame()"` (stand/creation/README.md) | - | - |

`statue.lua` (a figure behind the statue) and the two dancers are kept and are not part of the
video: the company took the statue's place on the splash screen, and Orin the dancer's under the
ground.

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
dummies are not. It stands in its animation blueprint's idle; no timeline plays an animation on
it. `Ext.Entity.GetAllEntitiesWithComponent("TLPreviewDummy")` does not list it: a scene keeps the
entity it was given.

**Animation.** A figure carries a character's animation blueprint, and the client can send it
what gameplay sends a character's: `Ext.ClientVisual.PlayAnimation(entity, start, loop, finish)`
calls `code:ls::AnimationBlueprintSystem::TriggerGameplayEvent<EoCGE::ASAnimation>`, the event of
the client's "play an animation" action state (`code:ecl::ASAnimation::Update`);
`Ext.ClientVisual.StopAnimation(entity)` calls the cancel event's twin. The three lists hold
animation short names; an animation resource becomes one when it is linked into the figure's
plain animation sets under its own id (`orin.lua`, `link`). What the blueprints raise is read
from two singletons that hold it for one frame: `AnimationTextKeyEventsSingleton` (a playing
animation's text keys) and `AnimationGameplayEventsSingleton` (an animation that is over, or
stopped, raises events for its entity). Root motion is written into the entity's `Transform`,
and while a blueprint plays the visual follows `Transform`: a scene that moves or turns an
animated figure writes `Transform` and the visual both. A figure of the two templates `orin.lua`
uses looks along -z when it is not turned. An animation named `..._SCENE_...` is authored in its
cinematic's space and plays metres off the figure's place.

**Effects.** `Ext.ClientVisual.PlayEffect(name, position, rotation, scale, levelOf)` calls
`code:ls::EffectsManager::PlayEffect` and answers a client entity with `Effect`; the key is the
effect's name, not its resource id. `entity.Effect.Flags` says `Playing`, then `Stopped` when an
effect that does not loop is over; `Ext.ClientVisual.StopEffect(entity, "Destroy")` takes it away.

**Hiding.** Writing a figure's `CullFlags` or `VisualFlags`, or giving it
`ClientInvisibilityVisual`, hides nothing. A scene removes the figure, or writes a scale near
zero to `Transform` and `Visual:SetWorldScale`.

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

## orin - Orin kills a townswoman, turns to the opening door and vanishes

    ./gatekit --instance <copy> exec client "$(cat stand/menu/orin.lua)"       # arm
    ./gatekit --instance <copy> exec client "$(cat stand/menu/orin_off.lua)"   # disarm

Client context; armed on the splash screen or in the menu, it fires when the menu's camera has
come down and stands, as `dancer_new` does. Two figures of the scene's own stand before the
camera, to the picture's right of its middle (`ahead`, `right`, `apart`, `down`), lit by a
borrowed torch light: `28c8bd46-...`, another torch's than `dancer_new` takes, so the two scenes
can stand together (`light`, `light_toward`, `light_up`).

- **Orin** is the root template `Trailer_Dream_Orin`: the template her actor is shape-shifted
  into in the dialogs `WYR_OrinsImpersonation_*` (`gatekit data timeline
  WYR_OrinsImpersonation_OrinFlamingFist`, the `TLShapeShift` track). **The victim** is
  `Humans_Female_Civilian`.
- **The kill.** The victim cowers in `HUM_F_Rig_DFLT_CINE_Fear_Loop_01` for `fear` seconds; Orin
  plays her own `HUM_F_Rig_DWS_CMBT_Skill_Orin_DeathbringerAssault_01_Attack`; at its first `Cast`
  text key the victim plays `HUM_F_Rig_DFLT_DIE_Physical_Stationary_Front_01` and is held in its
  last frame: `settle` seconds before the animation's `Duration` has passed, her `AnimationUpdate`
  component is removed, and a figure without it is no longer animated (giving the component back
  resumes it). `HUM_F_Rig_DFLT_DIE_Generic_Deathpose_01` is not a loop to lie in: its fourteen
  frames are fourteen different death poses, and played as a loop it throws the body about. The
  attack is over when the blueprint says so.
- **The door** is in the level: the scenery actor `13045dab-...` of `MM_Accept_Temple` is a
  timeline dummy whose visual is `DOOR_Bhaal_Stone_Double_Big_A`, the slab in the temple's gate
  behind the adventurers. The timeline lifts it by about thirteen metres in about twelve seconds,
  beginning as the camera comes down. The scene reads the dummy's `Transform` every tick: the
  door opens when it has risen `door_rise` metres above where it stood when the scene began.
  After the kill Orin waits for that, turns to the door in `turn` seconds and looks for `look`.
  `GKScene.menu_orin.door = { found, start, now, openedBy }` says what was read; a door that did
  not move in `door_wait` seconds stood open already, and the state says so.
- **Vanishing** is what she does in those dialogs when a disguise is dropped: the animation
  `HUM_F_Rig_SCENE_WYR_Orin_TwistRing` (a twist of her teleport ring), `burst` seconds into it
  the effect `VFX_Cinematic_Orin_Teleportation_01` at her feet, and `cover` seconds later the
  figure is removed. The effect entity is destroyed when its flags say `Stopped`.

The victim's body and the light stay until the scene is disarmed or the menu is left; the off
script removes the figures and the effect and puts the light back. State: `GKScene.menu_orin =
{ armed, fired, stage, waiting, error, door, log }`, `stage` one of `loading`, `fear`, `attack`,
`killed`, `turning`, `looking`, `ring`, `burst`, `gone`.

Rough: `fear`, `look`, `burst` and `cover` are the scene's own beats, counted in seconds; the
moment of the burst inside the twist is set by eye, not read from the dialogs' timelines. The
door has usually begun to rise by the time the kill is over, so she turns as soon as she is done.

## company - the Emperor and intellect devourers beside the statue on the splash screen

    ./gatekit --instance <copy> exec client "$(cat stand/menu/company.lua)"       # arm
    ./gatekit --instance <copy> exec client "$(cat stand/menu/company_off.lua)"   # disarm

Client context, armed while the splash screen stands. Four figures of the scene's own stand on
the grass about the statue, their backs to the watcher, looking at the city, which lies straight
ahead of the splash's camera: the Emperor in his mind flayer form (the root template
`Mindflayer_Emperor`) to the statue's left, two intellect devourers (`Intellect_Devourer`) before
him and one to the statue's right. Each plays an idle of its own kind as a loop
(`MFL_M_Rig_DFLT_IDLE_Random_Peace_02`, `INTDEV_Rig_DFLT_IDLE_Random_Peace_01`). `face = "statue"`
turns them to the statue instead. The places are given from the camera (`ahead`, `right`, `down`
for each member), clear of the "press any key" text. The splash screen is in daylight: no light
is borrowed, so the scene stands together with `orin.lua` and `dancer_new.lua`. The figures are
removed the moment the splash screen is passed. State: `GKScene.menu_company = { armed, fired,
waiting, error, done, members, log }`; options and the members' table: the file's head.

Rough: the ground's height under each member is a number set by eye (`down`), not read from the
level; the devourer to the statue's right stands in tall grass and is half hidden by it.

## statue - someone behind the statue on the splash screen

Kept, and not part of the video: the company stands on the splash screen instead.

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
