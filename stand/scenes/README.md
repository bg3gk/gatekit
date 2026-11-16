# The stand's scenes

## The driver's order of the whole video

`S`, `C` and `U` are `./gatekit --instance <copy> exec server`, `client` and `ui`. A scene is
armed by running its file and disarmed by its `_off` file; a load restarts Lua, so a scene of the
prologue is armed again after every `reset`.

Nobody appears while the watcher sees the place: every figure that is not a beat of its scene is
made under a cover - the logo over black, the descent of the menu's camera, the film of a new
game, the distance of a far deck - and stands in its pose before the place is first seen. The
column "Cover" says which.

| # | Beat | File, context | Arms it | Cover | The driver calls or waits for | Takes | Rough |
|---|---|---|---|---|---|---|---|
| 1 | the channel's logo | `stand/splash/logo.lua`, `U`, with `GKSceneOptions = { splash_logo = { hold = { 'menu_company', 'menu_orin' } } }` | at the first answer of the bridge after `up --splash` is begun (the client's Lua lives from the state `LoadModule`, after the game's own startup video): `up --splash` in the background, `U` asked until it answers | - (it is the cover) | `GKScene.splash_logo.left` | at least its fade and three seconds, and until both scenes of 2 and 4 say `ready` | the game's own startup video plays only in a copy given the video file (stand/splash/README.md); not in `gatekit shot` |
| 2 | the splash screen with the company | `stand/menu/company.lua`, `C` | right after 1, while the logo stands | the logo | `GKScene.menu_company.ready` (made, loaded, in their idles) | stands until the key | places set by eye |
| 3 | Orin and her victim, made | `stand/menu/orin.lua`, `C` | right after 1, while the logo stands | the logo, then out of every view under the ground | `GKScene.menu_orin.ready` (`stage` = `standing`) | - | - |
| 4 | "press any key" | - | - | - | `U "return GK.Cheat.Recover()"`; `GK.Gui.Stuck()` answers `mainmenu` | at once | - |
| 5 | the descent, Orin's kill | (3) | - | - | `GKScene.menu_orin.done`; the body stays until the menu is left | about twenty seconds from the key | her beats counted in seconds |
| 6 | creation, the lanceboard player | `stand/creation/onlooker.lua`, `S` | while the film of a new game plays, once the server is `Running` in `SYS_CC_I` (else at once after the film is skipped) | the film | `GKScene.onlooker.fired` before the film is skipped; the window's steps are in `stand/creation/README.md` | as long as the creation | arming under the film is not yet seen in a run |
| 7 | the stargazer's ambush | `stand/creation/scenes/stargazer_ambush.lua`, `S` | as 6 | the film | `stage` = `looking` before the film is skipped; the ambush itself by `go()` | the file's head | the bugbear's coming up behind her is the ambush's beat, with no effect |
| 8 | Orpheus | `stand/creation/scenes/orpheus.lua`, `S` (with `solid.lua`, `C`) | as 6, shown | the film | `chained` before the film is skipped | from the start of creation: he hangs far in the sky on every page | `show()` on the guardian's page alone made him appear before the watcher |
| 9 | the prologue, the watchers | `watchers.lua`, `S` | at the prologue's start, again after every `reset` | distance: both stand far from the party (the first dialogue of the prologue runs meanwhile) | the agent is told to go up to each; `GKScene.watchers.who.<name>.vanished` | four seconds each | Raphael's platform is off the shortest way |
| 10 | Withers at the transponder | `withers.lua`, `C` | at the prologue's start, again after every `reset` | distance: the helm is about 75 m from the party until its door | `GKScene.withers.stage` is `held` | - | the section below |
| 11 | the queen's answer | `queen.lua`, `S` | after the first fight | - (Vlaakith's arrival is the beat) | a checkpoint while `GKScene.queen.ready`; the agent shoves Lae'zel; `done`; `reset` | eleven seconds, then the death screen | - |
| 12 | skill issue | `skill_issue.lua`, `S` | before the helm, after the reset of 11 | - (the polymorph is the beat) | the agent casts Command: Drop at Zhalk; `awaiting = "reply"`; `GKScene.skill_issue.reply()`; `done` | about twelve seconds after the reply | - |
| 13 | the finale | `finale.lua`, `S`; `finale_credits.lua`, `U` | in the helm's fight, both before the owner's command | - (the brain's arrival is the beat) | on the command `S "return GKScene.finale.go()"`; `GKScene.finale.done`; then nothing | ten seconds to the credits, plus `black` | the section below |

Where the owner's wishes changed, the later one is followed: Orin under the ground replaced the
dancer to the right of the menu (`dancer.lua`, `dancer_new.lua`); the company replaced the figure
behind the statue (`statue.lua`); the lanceboard player and the stargazer replaced a plain
onlooker in creation; the stargazer's ambush replaced her standing at the telescope
(`stand/creation/stargazer.lua`); the thrown barrel replaced the brain blowing itself up with a
recoloured effect, which replaced pressing the transponder. The replaced files are kept and are
not part of the video.

**The run is over** when `GKScene.finale.done` is true: the credits roll, the game goes to its
main menu by itself when they end, and the driver does nothing more - no key, no call, no reset.

## How a scene of the prologue is made

Each of `watchers`, `queen` and `skill_issue` is one Lua chunk that **arms** a scene on the server and
returns at once. The scene then fires by itself when its condition comes true in the game, however
the agent got there, and fires once. Each keeps its state in a global table `GKScene.<name>` for
the bridge's `now.scenes` (mods/AI): `armed`, `fired`, `log` (`{ tick, ms, text }`, in order) and the fields below.
`<name>_off.lua` disarms it and takes back what it made.

    ./gatekit --instance <copy> exec server "$(cat stand/scenes/<name>.lua)"       # arm
    ./gatekit --instance <copy> exec server "$(cat stand/scenes/<name>_off.lua)"   # disarm

A scene needs a stage scope (a rig, the spawns' ledger): it uses the stand's when one is open and
otherwise opens its own (`GK.Stage.Begin`, fresh after a load). A load (`reset`) restarts Lua:
arm again after every reset. Lines are `GK.Say.Line` over the speaker's head; each step that
matters gets `GK.Camera.Focus` (with a hold); nothing opens a window.

## watchers - Raphael and Mizora watch the party

- Places: Raphael on Myrnath's platform above the surgery room `[0.8, 23.5, -429.2]` (seen from the
  room, 9-20 m), Mizora in the helm's antechamber by the restoration pod `[0.7, 18.2, -389.0]`
  (on the path to the helm door). Other places: `GKSceneOptions = { watchers = { raphael = {x,y,z},
  mizora = {x,y,z} } }` before arming. Raphael is his root (a bare human) transformed into the
  placed object `S_GLO_Monitor` (his look; his name stays "Человек").
- Every 0.3 s: a party member within 25 m whose sight holds one (`GK.See.Perceives`) marks it
  `seen` (`who.<name>.seen, seenBy`); `GKScene.watchers.notice` is then a line the bridge tells the agent
  ("someone is watching from afar (Рафаил)"). Each turns towards the nearest party member.
- Within 6 m: +0.6 s the camera on it (4.6 s), +1.1 s its line, +3.9 s the puff
  (`VFX_Script_Stub_Poof_01`, as Raphael vanishes in the game), +4.2 s removed.
- Lines: Raphael «Восхитительно. До скорой встречи.», Mizora «Какой забавный червячок.»
- The agent must be told to go up to them: Raphael's platform is off the shortest way.

## queen - the shove, and the queen's answer

- Trigger: Osiris `UsingSpellOnTarget` with a spell `Target_Shove*` by a party member at Lae'zel
  (it comes before the hit, in and out of a fight); `CastedSpell` ends it. Between the two the
  shove's two `Force` functors throw 14 m instead of the game's `ShoveDistance` (a shove of an
  ally has no roll: it always lands); put back at once after.
- `GKScene.queen.ready` (and `readyWhy`): armed, out of a fight, Lae'zel alive in the party and a
  party member within 4 m of her. **The driver makes a checkpoint while `ready` is true** (a scene
  cannot make the host's checkpoint from Lua), and **resets to it after the death screen**
  (`done` = true). The death screen ("Конец игры") stays until that reset.
- What the watcher sees (from the shove): 0 s the shove; Lae'zel cries «Предатель!..» and flies
  ~14 m; 0.7 s the camera follows her; 2.4 s a puff where she landed and she is gone (off stage);
  3.2 s the camera back on the shover; 4.6 s a misty-step flash and Vlaakith stands 2 m beside
  him, turns to him, the camera on her; 5.9 s «Ты за это ответишь.»; 9.2 s Power Word Kill, the
  red column, the shover dies; Lae'zel dies off stage right after (the whole party dead), and the
  death screen comes (about 1.5 s later).

## skill_issue - Command: Drop at Zhalk

- Armed: a rig on Zhalk's saving throws against `Target_Command_Drop` (natural 20, every one
  while armed). Trigger: `UsingSpellOnTarget` of `Target_Command_Drop*` at Zhalk by a party
  member, then `CastedSpell`, then the rig's count rising (he saved).
- +1.2 s the camera on him and a glow; +1.7 s «Skill issue, сосунок.»; then
  `GKScene.skill_issue.awaiting = "reply"`. **The driver calls `GKScene.skill_issue.reply()`**
  once the agent has said its answer (`exec server "return GKScene.skill_issue.reply()"`).
- After the reply: the camera on Shadowheart, +0.5 s «Как невежливо.»; +3.6 s she casts a polymorph
  (her own cast animation; a spell of the scene's own, a copy of Polymorph with no save, no cost,
  no concentration) whose status is a copy of the cheese wheel's polymorph named «Какашка»: he
  becomes a smelly cheese wheel, the name floats over him, the combat log says
  "получает состояние «Какашка»", three dung items (`LOOT_GEN_Poop_A`) lie at his feet; +1.1 s
  the camera on him; +3.6 s Shadowheart «Вот теперь ты пахнешь честно.» The polymorph lasts 5
  rounds; he is immortal while it lasts (its one hit point would end it at the first blow).
- The fight goes on: the helm's countdown went on (15 -> 14), his turn passed as the wheel, and the
  transponder took the party to the next level as usual.

## withers - Withers in a dab beside the transponder

    C "$(cat stand/scenes/withers.lua)"       # arm
    C "$(cat stand/scenes/withers_off.lua)"   # disarm

Client context, `TUT_Avernus_C`, armed at the prologue's start (and after every load): he stands
long before the party comes to the helm. A figure of the scene's own (`Ext.ClientVisual.CreateDummy`,
the root template `S_GLO_JergalAvatar`) stands at the transponder, 2 m to its north-east
(`-81.0, -387.5`, the floor's height read by a ray), turned to the helm's door, and holds a dab
that is a table of the scene's own, the spine leaning a little to his right, toward the
transponder; the bent arm crosses in front of the face. The helm is dark: a point light of the
level that carries `LightMovement` (`8e89e599-...`, from a deck far under the helm) is put before
his face and made brighter (`Intensity` 20000, `Radius` 5); both go back when the scene is
disarmed.

**Breathing** (`breathing = true`, the default): his own idle plays, and every frame the pose is
laid over it after the client's `Effect` system - the leaning bones turned in their frame of the
frame, the arms and the head held as posed on the living chest - so the body breathes and shifts
while the dab holds. The skinning buffers the game fills are rewritten in the frame it fills them
(it fills one of each binding's two, in turn; both are watched). Before he is first seen he holds
the dab written from the rest pose, so no frame of the plain idle shows. `breathing = false`
writes the dab once and stops his animation (a statue). Both need GateKit's Script Extender in
developer mode (`Ext.Debug.ReadMemory`, `WriteMemory`, `GetObjectAddress`).

`stage` is `held` once he stands posed. Options: `at`, `yaw`, `pose`, `lean`, `breathing`, `move`,
`light`, `light_intensity`, `light_radius` and the others of the file's head.

He is a watcher of the finale and it cannot touch him: the figure is on the client only, the
server holds no creature for a blast, a hit or a shove to reach.

Rough: breathing is Lua writing memory every frame: while he is in the picture it costs about 30 ms
a frame (the frame rate fell from about 30 to about 25 with the camera on him); out of the picture
nothing is written. A native bone override in the extender would cost microseconds.

## finale - the barrel, the black screen, the credits

    S "$(cat stand/scenes/finale.lua)"               # arm; nothing happens in the game yet
    U "GKSceneOptions = { finale = { credits = { ... } } } $(cat stand/scenes/finale_credits.lua)"   # optional: the stand's own block
    S "return GKScene.finale.go()"                   # on the owner's command
    S "$(cat stand/scenes/finale_off.lua)"           # disarm

Nobody uses the transponder. From `go`, `GKScene.finale.stage` goes:

| Stage | What happens | Ends when |
|---|---|---|
| `leaving` | every party member is barred from joining a fight (`Osi.SetCanJoinCombat(member, 0)`) and the fight is ended (`Osi.EndCombat`), the story's own pair for taking a creature out of a fight | `Osi.IsInCombat` of the character answers 0 |
| `coming` | only when the character is farther than `reach` from the brain's place: it walks there | it is within `reach` |
| `arriving` | the burst of the brain's last confrontation; the brain (`Elder_Brain_Crowned_Highres`, the template of the last level's `S_GLO_Netherbrain`, under that object's name) stands at `at`, 8 m from the transponder on the deck, turned to the character, at `scale` 2, the crown glowing; the camera on it | `behold` seconds |
| `arming` | the party gets `INVULNERABLE_NOT_SHOWN` and the character the cloud giant elixir's status; a Runepowder Barrel comes into the pack and its hit points are set to 1 | the barrel reads 1 hit point and the statuses are on |
| `throwing` | `GK.Cheat.Throw(character, barrel, brain)`, the game's Throw; the barrel breaks on landing and explodes by its own rule | the brain is dead (`GKScene.finale.death` = the game's row: who killed it) |
| `blast` | the explosion on the screen | `after` seconds |
| `fading` | `Osi.ScreenFadeTo`, the story's fade to black; the black held for `black` seconds | the Osiris event `ScreenFadeDone` |
| `credits` | `PROC_StartMovie("Credits")`, the story's line at the end of the game; `done = true` | - |

What is staged, and why:

- **Leaving.** The game's own Flee is refused in the helm (`GK.Act.Flee`: "this place does not
  allow fleeing"). With `companions = "fight"` only the character is barred: the others are at
  once in a new fight, which waits on their turn.
- **The helm's creatures** begin a fight of their own, Zhalk against the mind flayer. The story's
  clock - the fifteen rounds, the cambions of round 6, the crash at round 16 - counts the rounds
  of the fight that was ended, so none of it comes.
- **The brain's hit points** are set to `hp` (20 of 275) with `Osi.SetHitpoints`, so that one
  barrel kills it: a barrel deals 50 to 120, half on a saved throw.
- **The barrel's hit points** are set to 1: a thrown barrel takes 1 to 4 from the throw and
  explodes only when it breaks.
- **The strength.** A barrel weighs 16 kg and the game refuses a throw by a weak character ("the
  thrown thing weighs 16000"): the elixir's status is the game's own way to be strong enough. Its
  name floats over the character once.
- **Nobody of the party is hurt or thrown**: the status is the story's `PROC_SetInvulnerable`.

**The credits and the end.** The credits are the page the main menu's Credits button opens,
started as the story starts them. The page closes when it has rolled out or when its own command
`CreditsEnded` runs (its close button; `U "return GK.Cheat.Run('Credits', 'CreditsEnded')"`), and
by itself it only raises the Osiris event `CreditsEnded`: started in a level, the game is back in
that level when they close. It is the story that leaves the session: it answers the event with
the Osiris call `GameEnd` (`PROC_GameEndWithMovie("")` at Moonrise; the last goal calls
`GameEnd` after its closing scenes). The scene answers the event with the same line, and the game
unloads the level and stands on its splash screen, in the state `Menu`. With `menu = false` the
scene does not, and the game stays in the level under the black screen.

**The stand's own block at the head of the credits** (`finale_credits.lua`, UI context, armed
before `go`; with no options the credits are the game's own):

    GKSceneOptions = { finale = { credits = {
        logo = "Stand/gatekit-logo.png", seconds = 4, gap = 4,
        lines = {
            { text = "GateKit", bold = true, size = 72, color = "gold" },
            { text = "MAP · EXTEND · AUTOMATE" },
            { text = "From Gwynerva, to the game I love most" },
            { text = "Made entirely with AI" },
            { text = "Built on Norbyte's Script Extender" },
            { text = "and on the study of the community's mods" },
        } } } }

and on the server `GKSceneOptions = { finale = { black = 6 } }` before arming `finale.lua`, the
picture's seconds and fades.

- **The picture is the stand's.** The credits page has no picture entry: its view model holds
  text lines and sections only. The logo is drawn by `GK.Show.Picture` on the black screen before
  the credits start, from a file under the copy's `Data` folder (once per copy:
  `cp assets/brand/logo-round-512.png "<copy's game folder>/Data/Stand/gatekit-logo.png"`). It is
  not in `gatekit shot`.
- **The lines are the credits' own first entry.** The credits are the file
  `Public/Game/Credits/credits.txt`, lines of the game's markup (`<font size='42'>`, `<br>`), read
  into the page's view model. A line of the view model is read-only for the extender, so the text
  is written into the page's first text block, which is an empty line: plain text, `bold`, `size`.
  All the lines are that one block, so they share the first entry's look. The block ends with
  `gap` empty lines (4), which part it from the game's own first line ("Larian Studios"). A colour is one of the
  page's own brushes: `"gold"`, or the credits' white. A brush cannot be made from Lua, and a
  brush's `Color` written from Lua reads back unchanged: no violet, and no line of its own colour
  or size.
- **A mod's way** gives all of it: a `credits.txt` of a mod overrides the game's, and every line
  takes the markup the file already uses.

Rough: the throw's attack roll is the game's and is not decided; `black` is counted in seconds,
the UI part reads the black from the HUD's fade and the server does not hear when the picture
left; the walk of `coming` and the fight of `companions = "fight"` are written and were not run to
the end.
