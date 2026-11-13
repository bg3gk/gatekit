# An agent plays the Nautiloid: journal

Goal: an agent plays the prologue (level `TUT_Avernus_C`) without screenshots - it reads the world
through GK.See and acts through GK.Act - at accelerated game time. Two runs: guided (bg3.wiki as
context) and blind (only "this is BG3, D&D 5e rules"). The API is described in
[situation.md](situation.md). Newest entries at the bottom.

## 2026-11-13: building the toolkit

**Prior art first.** `Bogzx/bg3-agent-harness` (36k lines of Lua on bg3le, the Linux SE) was read
for its recipes; the most useful findings, all re-checked here:

- Perception = the engine's `Osi.CanSee` (a 90° cone of the character's eyes, `Sight.Sight` about
  18.5 m, light, stealth) OR a clear line of sight within the sight range (the player's camera
  looks around freely). Taken over as is; the line is our own ray (`Ext.Level.RaycastClosest`,
  static + dynamic geometry, without characters and see-through shapes).
- **Osiris casts skip their dice.** `Osi.UseSpell` puts `IgnoreSpellRolls`, `IgnoreCastChecks`
  and `IgnoreTargetChecks` into the cast's options. Checked here: a raw `Osi.UseSpell` hit and did
  not spend the action (Astarion still had 1/1 action). GK.Act catches the cast's
  `SpellCastState` in `Ext.Entity.OnCreate` and takes those three flags off: then the attack
  rolls (3 misses of 4 against a goblin given AC 28) **and the engine charges the action itself**
  (`IgnoreCastChecks` was what skipped the cost).
- `Osi.UseSpell` with five arguments (target2, WithoutMove) was accepted and did nothing on
  4.1.1.7631656; the three-argument form casts.
- `Osi.CharacterMoveTo(object)` teleports when there is no path; walks go to a point the
  pathfinder proved (`Ext.Level.BeginPathfindingImmediate` + `FindPath`, nodes summed).
  An Osiris walk in a fight **is** charged by the engine (9 -> 7 m after a 2 m walk): GK.Act only
  charges what the engine did not.
- Talking: raising the event `DialogStartRequested(npc, player)` is what a click on the NPC does;
  the story then picks the dialogue. `QRY_StartDialog` from afar is a cheat.
- Osiris event arguments carry the template-prefixed name (`S_Player_Astarion_c7c1...`): compare
  by the trailing uuid.
- Listeners registered with `Ext.Osiris.RegisterListener` keep their closures across a reload of
  one file: handlers must be looked up at call time (`GK.Act.On[name]`).

**Facts of the level** (Astarion as the host, started straight on the level by the stand):
north = +z, east = +x (the wiki's map coordinates are X = x, Y = z). Using the nursery pool starts
a narrator dialogue with three answers. The fight's turn order also lists scripted objects of the
level at initiative -20 (pods, a tablet, a chest): GK.See.Combat leaves them out.

## 2026-11-13: run 1 - guided (bg3.wiki as context), Astarion

**Result: the prologue was completed.** Started 09:24 on a fresh `TUT_Avernus_C` (Astarion, the
stand's start), the transponder was used at 10:24 and the game loaded `WLD_Main_A`. One hour of
wall clock, most of it spent building the fixes listed below during the run; no screenshots, no
cheat, no reload. Game speed: `speed auto` (exploration and enemy turns 4-6x, own turns 1x).

Route (the wiki's walkthrough of "Escape the Nautiloid" as context): nursery (loot the dead
illithid: topaz; a chest: 15 gold; onyx; read a rune slate - the narrator's vision was caught by
GK.Heard) -> the sphincter door west -> surgery room (goblin corpse, illithid record) -> the
elevator switch on its platform rode up -> Myrnath / "Us": Arcana passed passively, a Dexterity
check (18 vs DC 10) freed the brain, "Us" joined -> west along `ways` until Lae'zel's scene started
by itself -> **first fight**: three imps, won without losses (Lae'zel's Slash, Astarion's attack and
Fire Bolt, Us's claws) -> two arterial meshes climbed (ladders) -> a door -> Shadowheart's room ->
the dead thrall in the east chamber had the rune (and a chest two healing potions) -> console: insert
the rune, touch it, [Illithid][Wisdom] check (13 vs DC 2) opened the pod -> Shadowheart joined ->
the Helm: the countdown (15 turns) appeared; Shadowheart's Guiding Bolt killed the hellsboar, an
imp fell to Lae'zel's shot; Astarion dashed past Zhalk (busy with the mind flayer) two rounds in a
row and used the transponder on round 5 with 11 turns left, at 4 HP (an attack of opportunity on
the way - he should have disengaged; the API now refuses such a walk unless asked).

Problems met, and the general fix each became:

| Problem | Fix (all in the API, documented in situation.md) |
|---|---|
| Osiris casts never roll; costs skipped | Ignore* cast options stripped on creation; costs the engine skips are charged |
| `UseSpell` with 5 arguments does nothing | the 3-argument form |
| Loot walk put Astarion on top of a corpse on a table; no path from there | approach prefers the walker's floor; `Stuck`/`Unstick`; a jump got him down |
| Elevator switch on a moving platform: "no path" | `Use` approaches as close as walking allows and lets the game decide (curious clicks) |
| Doors stand in walls: no ring point reached | wider rings for doors; path-end tolerance |
| Rune slate's narrator text and barks over heads unreadable | `GK.Heard` from the dialog manager |
| A lone "Leave" read while the answer list was still filling in walked out of a real choice | lone answers re-read after 0.7 s; a lone Leave stops the walk-through |
| Party members share one turn; `Osi.EndTurn` dropped for them | open turns listed per member; End Turn through the UI button; select first |
| A table referenced twice in one answer came out empty in JSON | rows are copied |
| Spell slots arrive as a JSON list | handled |
| Big objects (tanks, doors) hit by the sight ray on their surface | a ray hitting the target's own body counts |
| No way to see which way leads on | `GK.See.Ways` |
| Odds of attacks, saves and checks unknown | `Odds`, `Targets`, `CheckOdds`, roll window chance |
| The countdown at the right edge unread | `GK.See.Timers` (StoryTimer component) |
| The camera stayed put | `GK.Camera` faces each command's target, smoothly |
| Walking out of melee provoked an attack | walks that provoke are refused unless asked; Disengage first |
| "No path" was a dead end | `Act.Alternatives`: nearest walkable point, the gap, jump and teleport spells with range |

Facts learned on the way: the turn order also holds level objects (initiative -20); the engine
charges movement for Osiris walks in a fight, and the action for casts once the Ignore options are
off, but not always the spell slot (Guiding Bolt's slot was charged by GK.Act); `Osi.IsControlled`
follows the UI selection; `MakePlayerActive` selects a member; the game's own End Turn selects the
next member of the group; the elevator is a `LevelInstance` entity with a `Platform` component and
`Osi.GetPlatformUnderObject` names it; the helm countdown is `eoc::story::TimerComponent`
("TUT_Helm_Timer", 90 s = 15 turns, text key `TUT_Helm_TransponderTimer`).

## 2026-11-13: run 2 - blind (no wiki, no game files, only the API), a subagent

**Result: the prologue was completed as well.** The blind player reached the transmitter in time;
the level changed to `WLD_Main_A` with nobody dead. About 170 game commands (201 tool calls) in
42 minutes of wall clock (10:45-11:27), while the interface was being changed under it.

A fresh Opus subagent got one command (`play.sh`, a wrapper over `stand/situation.py` in strict mode:
no omniscient look) and a brief: BG3, D&D 5e rules, play as a newcomer without memorised
walkthroughs, keep `notes.md` and a list of every inconvenience and wish. The developer (this
session) watched the wishes file and turned each fair wish into a general tool while it played,
messaging the player after each update. Game restarted fresh at 10:40 (Astarion, `speed auto`).

Wishes and what they became (the player numbered them; verdicts are the developer's):

| # | Wish (blind player, abridged) | Verdict | What it became |
|---|---|---|---|
| 1 | `help` exits 2, names the python path | fair | help exits 0 and names the wrapper (`GK_AGENT_CLI`) |
| 2 | no name of the place | fair | `GK.Places.Zone()` (HUD's subregion; empty on the Nautiloid) in `status` |
| 3 | examine shows no description, no idea what "use" does | fair | `See.Entity`: template description + `use` verbs |
| 4, 15 | a dialogue ended by itself with "Leave" | bug (ours) | the walk-through never picks a lone Leave; re-read before picking a lone answer |
| 5 | roll result unclear | fair | one line: d20 X + bonus = total vs DC: success |
| 6 | loot "close" took everything | other session's (Popups) | reported to the GK.Gui session |
| 7 | pickup said failed but worked | bug (ours) | owner compared by uuid |
| 8 | no inventory | fair | `See.Inventory`, `inventory`; carried items examinable |
| 9 | use said opened=false on an open door | bug (ours) | effect read after the animation, in words |
| 10 | book text not shown | partly | narrator lines of read items arrive in `heard`; real books: open |
| 11 | ways and move disagree | environment | under a raised elevator platform the height map is the platform's; documented |
| 12 | look hides things that left sight | fair | `look known` (memory) and `kinds=` filter |
| 13 | stuck on an object, no hint | fair | `status` warns "cannot walk from here: jump" (Act.Stuck) |
| 14 | heard has no direction or id | fair | direction, id, position of the speaker |
| 16 | commands in a dialogue: no text; tracebacks | fair | refusals show the dialogue; errors as one line |
| 17, 20 | combat broke ('str' has no 'get') | bug (ours) | a row met twice came out as `*RECURSION*`; `seq()` |
| 18, 19 | actions <who> <target> read wrong | fair | first id is who |
| 21 | wait timed out during enemy turns | fair | `wait` waits for the party's turn, retries a busy server |
| 22 | cast did not walk into range | fair | cast/attack walk first by default |
| 23 | combat offered Sneak Attack the game refuses | fair | `GK.Conditions`: the spell's own conditions evaluated; refused early with the failing part |
| 24 | loot did not say what was taken | fair | `resolve` loot reports what was received (inventory diff) |
| 25 | quest marker without direction | fair | `GK.Places.Markers()` + `markers`, objectives in `status` |
| 26 | ladder "climbed" but did not | bug (ours) | judged by height, retried; a character at the foot steps back and walks in again |
| 27 | own character remembered as a ghost | bug (ours) | party never remembered; `kinds=` filters characters |
| 28, 29 | dialog/answer crashed | bug (ours, editing live) | fixed; `situation_smoke.py` runs before every change |
| 30, 32 | ladder still failed; characters moved by themselves | bug (ours) + the developer's debugging | clearing the Osiris queue early broke the climb off; now the climb runs, is stopped on arrival, companions are unchained for it. The "moves" were the developer's tests on the same game |
| 31 | whose view look/ways are | fair | named; `look who=` |
| 33 | wait failed with "no answer" | bug (ours) | the footing check (path searches) ran twice a second inside waits; waits now use a light status |
| 34 | ids from heard not usable | fair | registered as short ids |
| 35 | loot on a backpack said "it is not dead" | fair | loot on a container opens it |
| 36 | no way to insert an item into an object | explained | the game offers it as a dialogue answer to whoever carries the item |
| 37 | no lockpicking | gap | the engine entry points found (RequestPickLock, RequestDisarmAttempt), a fork binding is needed; attack breaks locks meanwhile |
| 38 | attack used the bow instead of walking to melee | fair | melee when the walk fits the movement; `attack <id> melee/ranged` |
| 39 | a WinError 5 on sending a command | fair | the command-file rename is retried |
| 40 | popup timed out during the level change | fair | `status` answers "loading", `wait` returns when the level is playable |
| 41 | quest ids instead of titles in events | fair | titles and step texts from the game's quest prototypes |
| 42 | countdown only in combat | already there | `status` lists timers; a time-limit warning for side tasks is not done |

The blind player's own account (abridged): goals set in order - loot the pod room, find the exit,
answer the cries for help (it recognised the intellect devourer and destroyed the brain, unlike
the guided run), join Lae'zel, reach the helm marker (`markers` was "a breakthrough"), try to free
Shadowheart (it could not find the rune, the countdown made it leave her: quest closed
"LeftBehind"), reach the transmitter. Fights: three imps (won, Astarion -1 HP); at the helm a
hellsboar, four imps and Zhalk - Zhalk downed Astarion after the mind flayer died, Lae'zel used
Second Wind, disengaged twice and used the transmitter at 5 HP.

What cost it most: the ladder (about 15 commands, our bug), getting stuck on an object, the missing
lockpicking, and interface errors while it was being edited live (tracebacks, a broken `combat`,
two auto-"Leave"s). Its top wishes are in the table above; most are done.

## Comparing the two runs

| | guided (wiki) | blind (API only) |
|---|---|---|
| Result | prologue done | prologue done |
| Wall clock | 60 min (most of it building tools) | 42 min |
| Companions | Us, Lae'zel, Shadowheart | Lae'zel (Us destroyed, Shadowheart left behind) |
| Fights | 2 won, nobody down | 2 won, Astarion downed once |
| What decided it | the wiki's route and the rune's location | `markers`, `ways`, `heard`, the countdown |

Both runs show the same thing: with perception-filtered reading, odds and honest commands, the
prologue is playable without a screen; what blocks is the interface's own rough edges, which a
player reports at once and which become general tools.

## 2026-11-13: run 3 - blind again, with the improved API

A fresh Opus subagent, the same strict brief (plus a line naming `rules`, `markers`, `events`,
`heard`, `inventory`, `ways`, `jobs`), a fresh game at 11:16.

**Result: the prologue was completed in about 80 game commands and 20 minutes of wall clock,
with no damage taken in either fight** (run 2: 170 commands, 42 minutes, Astarion downed). It
looted the start (equipping a light crossbow found on the way), met Lae'zel, found the ladders via
`ways` connectors, and at the helm killed two waves of imps and hellsboars at range (crossbow hits
at 95 %, a ranged Sneak Attack) while routing around Zhalk; Lae'zel used the transmitter with 9 of
15 rounds left. It did not free "Us" or Shadowheart (the captive's quest was never announced to it:
it began with a sub-quest update the game log did not listen to - fixed).

Its 18 wishes, and what they became: the off-stage player stand-in counted as a party member (the
party is now on-stage members only); both weapon sets in `party`; roll events without the
criticality code; loot counts gold; `use` on carried items without walking, `read <id>` for books;
`dialog` shows the conversation from its start and the last one when none is open; a cheap turn
check for waits and `endturn all` (which says what it could not end); partial moves to unwalkable
goals walk to the nearest point, a too-far move suggests `partial`; doors and ladders seen to 30 m,
`ways` lists connectors; sub-quests announced as new quests; actions say when a used one
recharges, combat lists what is not ready; statuses carry their description; the countdown is in
rounds. Its notes and wishes: agent-play-blind3-notes.md / -wishes.md.

The 30 s "no answer" stalls seen during enemy turns (`agent-slow-calls.log`) were not the game:
the server kept ticking. The stand's channel had one command file and one answer file per side,
so two processes calling at once (the agent and a watcher) overwrote each other's command or
answer. Fixed in `stand/gatekit.py` (bb7f181: an append-only command queue, one answer file per
command); checked afterwards with 12 `situation.py` calls at once, all answered.

| | run 2 (blind) | run 3 (blind, improved API) |
|---|---|---|
| Commands / wall clock | ~170 / 42 min | ~80 / 20 min |
| Damage in fights | Astarion downed once | none |
| Companions at the end | Lae'zel | Lae'zel |
| New wishes | 42 | 18 |

## 2026-11-13: after run 3 - focus, saves, briefs; paused for the migration

Added after run 3 (all on main, each checked live on the Nautiloid unless said otherwise):

- **Visual focus** (`GK.Focus`, c1570ea, 63f7461): a violet pulsing outline from the engine's own
  outline renderer (`ls::OutlineComponent`, player id 99) on characters and items a command aims
  at or the agent examines; the game's destination ring / beam for positions; the game's pointer
  kept quiet while the window is in the background (picking helper off, CursorText collapsed).
  Not yet seen in a real fight; yielding to a real mouse untested.
- **Saves and loads with the whole state** (`GK.SaveState`, bf1d34c): `save <label>` (the quick
  save, copied to a labelled store with the agent's notes), `saves`, `load <label> [notes=restore]`;
  the bridge's memory (remembered objects, game log, heard and immersive lines, cheat log) rides
  in every save's PersistentVars.
- **Agent runs from parts** (`stand/agent_run.py`, `stand/agent/`, b2d587a, 5a9c5fe): wrapper +
  task (`prologue`, `explore`) + addons (`tester`, `newcomer`, `immersive`) + any prompts (text or
  files), starting from `--save`; no part names a place. `--run` (claude CLI) untested.
- The 30 s stalls were the stand's channel race (see above), fixed by the stand's owner.

State at the pause (2026-11-13): everything committed on main of <old repo>; the
agent game copy closed (`down`); labelled saves stay in `%LOCALAPPDATA%/GateKit/agent-saves/agent/`.
GateKit is being migrated (new monorepo layout, a C# host, Lua scenarios); this work resumes on
the new layout.

Next steps after the migration:

1. Port `stand/situation.py`, `stand/agent_play.py`, `stand/agent_run.py` and the MCP tools to
   the new host and CLI; the bridge Lua (`Server/Situation.lua`, `Act.lua`, `Focus.lua`,
   `SaveState.lua`, ... and `Client/Focus.lua`, `Camera.lua`, `Places.lua`) should move as is.
2. A blind run 4 with the new launcher:
   `agent_run --task prologue --addon newcomer --addon tester --immersive`, watching the focus
   outline in fights; then turn its wishes into tools as before.
3. A run of `--task explore` from a mid-game save, to check that nothing in the briefs or the API
   assumes the Nautiloid.
