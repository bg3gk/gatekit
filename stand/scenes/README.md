# The stand's scenes

Three staged surprises for the video, each one Lua chunk that **arms** a scene on the server and
returns at once. The scene then fires by itself when its condition comes true in the game, however
the agent got there, and fires once. Each keeps its state in a global table `GKScene.<name>` for
the stand's feed: `armed`, `fired`, `log` (`{ tick, ms, text }`, in order) and the fields below.
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
  `seen` (`who.<name>.seen, seenBy`); `GKScene.watchers.notice` is then a line for the feed
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
