# Chronicle: aiming

The executor's own log, appended as the work goes. Copy `aim`, branch `poc-aiming`.

## Rung 1: a clear aim, told truthfully

**Asked.** Bring `aim_and_observe` into `tests/aiming/` as tests of one rule each; find why it
fails after `kits_and_attention`; make `Aim` answer the same on every aim and `Confirm` answer
what it saw.

**Done.**

- Tests (`tests/aiming/`, suite `_suite.json`):
  - `cursor/hit_chance` - the cursor's chance over a paralysed creature is two rolls' chance,
    1 - (1 - p)^2 of one roll's p (bg3.wiki "Advantage", "Paralysed"); the ground shows none;
    the same aim made again reads the same.
  - `cursor/refused_target` - Fire Bolt at its caster: invalid with the game's reason, no click
    given, the targeting stays open; the same action at the ground is taken.
  - `area/preview_is_the_blast` - Fireball outlines the three goblins and not the ally 5.3 m
    away; the click answers that the cast started; the blast burns exactly the outlined.
- `Client/Aim.lua`:
  - `Confirm` answers `ok` when the cast started - the server's targets written into the
    caster's cast entity (`SpellCastState.Targets`) - with `cast = {spell, guid, targets}`;
    otherwise ok = false with what was seen (still open: the aim stays held; closed with no
    cast within a second). The button is released two ticks after the press (was a 120 ms
    timer), the aim given back when the cast starts or the targeting closes (was a 250 ms timer).
  - `Aim`: the same aim as the one held answers at once; the hit-chance line is recognised by
    the task line's own context byte (0x80); the settle also needs ten frames of the picking.
  - `Aiming` gives `base_hit_chance` (`HitChanceDesc.BaseHitChance`).
  - `CancelAim` is ok when the targeting of the action in hand closed: the game's idle targeting
    that replaces it (the main-hand attack when the real pointer lies over a hostile) no longer
    reads as "still open".
- Contract: lua-api.md section 11 and `lua-types/GateKit.lua`.
- Study: `research/studies/aiming.md`, rung 1.

**Proven.** In one session of copy `aim`, one after another, the scene cleaned and verified by
the ladder before each test and after the suite:
`gatekit exec server "GK.TestSpeed = 1"` then `test tests/aiming` (speed 1, 3 passed), then
`GK.TestSpeed = nil` and `test tests/aiming` (speed 8, 3 passed). Then `kits_and_attention`,
`aim_and_observe` and `tests/aiming` in that order: all passed. Runs
`20261115-035417-suite`, `20261115-035504-suite`, `20261115-035712-suite` under the copy's runs.

**Surprises listed by the runner.** None for the aiming tests at either speed. For
`kits_and_attention`: box, combat_started, dialogue, loot, reaction, roll, trade, each "began and
ended inside the test" - all real and all what that test means to cause; its header could allow
them (not mine to edit).

**The instability.** Not reproduced with the tree as it is. The failures on record (copy
`attacks`, 01:48 and 01:53) followed two tests whose clean failed with the host in a fight;
under `--attach` that world became the baseline, the stray hostile goblin with it, and Verify
called it clean. The study gives the evidence (the pictures: the turn order, a creature beside
the host; the verdicts of the runs before). Cured by the clean that carries strays and by the
initiative pin in `aim_and_observe`. Two things Verify still misses are proposals below.

**Findings.**

- finding: while an action is aimed the server keeps a cast entity with no targets; the click it
  takes writes `Targets` and `CastPosition` and adds `spell_cast::MovementComponent` in the frame
  the targeting closes. Evidence: a tick watch on the client around a click.
- finding: out of range and out of a fight, Fire Bolt's click starts the cast and the caster
  walks into range first; `SpellCastMovement.Position` stays zero.
- finding: the game picks from the real pointer every frame; in a background window the pointer
  stays where it last was, and the idle cursor (the walk, or the main-hand attack over a hostile)
  follows whatever lies there. Environmental: it differs between copies.
- finding (open, rung 2 and 5): a hostile goblin created and paralysed in one chunk and never
  moved is hidden from the party (`Osi.IsInvisible` 1, an `InvisibilityComponent` with its spot,
  `Osi.CanSee` 0, the aim "invalid target"); moved once in place it is seen. The tests move
  their creatures once and state "the host sees it" as a precondition.
- bug (ours, suspected): `GK.Stage` carries strays one Lua state deep; after two bridge reloads a
  creature spawned two states back enters the baseline and Verify is clean with it there.
  Evidence: a probe goblin of mine, spawned before two reloads, was outlined as a fourth target
  by the next test.
- not reproduced: "no hit chance on a second aim at the same target" - three aims, at speed 1
  and 8, after other aims and after a cancel, all read 80. Kept as a step of `hit_chance`.

**Where time went.** The edit tool refused the worktree at first (the session was bound to the
coordinator's worktree); entering this worktree fixed it. A probe goblin of mine survived two
bridge reloads into a baseline and failed the Fireball test once (the strays bug above).
Confirm's first frame-counted release ended too early (the picking's post-update runs more than
once a frame; the count is now of ticks).

**Crashes, hangs.** None.

**Wishes and to-dos.**

- To the coordinator (runner): `gatekit test --speed <n|working>`. Today a test can only choose
  its own speed; mine read `GK.TestSpeed` from the game (set with `exec`, lost on a bridge
  reload), which is a workaround, not a contract.
- To the coordinator (stage): strays read from every Lua state since the last clean, not only
  the one before (the bug above).
- To the coordinator (kits): `kits_and_attention` could allow its seven blocking states in its
  header.
- To-do (aiming): an event for the cursor's view model following a new target; the lead is
  `CurrentPlayer.CurrentTarget`, which damage-preview binds and the hotbar's data context lacks.
- Fork wish, already recorded: W-0126, setting `ls::Mouse` so the idle cursor is ours too.

**API review.**

- Improved: `Confirm` answers what the server did; `CancelAim` answers about the action it left;
  `Aim` repeats at once; `base_hit_chance` lets a test check advantage arithmetic.
- Removed: the two real-time timers of `Confirm`.
- Still a wait: `Aim`'s settle for a new target that reads the same (real ms and frames), the
  relation cache of 5 s, `aimRelease` on real ms. `GK.Observe`'s `within` is a real-ms guard.
- Cover additionally: a click the game does not take after a valid aim (none found to stage);
  Jump and Throw through the new `Confirm` (they make a cast entity too, not checked).
- Not touched by tests now: `GK.Cheat.AimBest` (rung 5) and the old idle-aim check of
  `aim_and_observe`, which depends on the real pointer.

**Announced change for the attacks executor (before merge).** `GK.Gui.Confirm`:

| Case | Old answer | New answer |
|---|---|---|
| the click starts the cast | ok = true, saw "the game took the click: the targeting of X closed", answered when the targeting closed | ok = true, saw "the cast of X started", `cast = {spell, guid, targets}`, answered when the targets arrive (the same frame in every case seen) |
| the targeting closes with no cast of it | ok = true | ok = false, saw "... closed and no cast of it started", after a second |
| the targeting stays open | ok = false, aim given back after 250 ms | ok = false, the aim stays held (Aim again, or CancelAim) |
| invalid aim, nothing aimed | ok = false, did = "nothing" | unchanged |

`GK.Gui.CancelAim`: old ok = false "still open" when the idle targeting (main-hand attack under
the pointer) replaced the one left; new ok = true, saw "the targeting of X closed".
`GK.Gui.Aim` and `Aiming`: unchanged fields and meanings; `base_hit_chance` added; the same aim
made again answers sooner.

**After the merge.** Rung 1's tests take their speed from `test --speed` (`GK.TestSpeed` gone);
passed again with `--speed 1` and `--speed working` (runs `...-041451`, `...-041535`, and after
the rung-2 changes to Aim.lua `...-045417` to `...-045528`). Aim's settle written as a gap with
its lead (`CurrentPlayer.CurrentTarget`).

## Rung 2: what blocks an aim

**Asked.** The rule from the wiki and wider; then the game with placed pieces: terrain, walls and
placed objects; which property makes an object block; for each kind of obstacle the aim blocked,
regained by stepping aside, and what the cursor reports; whether a placed piece joins sight and
navigation; sight, projectile and walking kept apart.

**Done.**

- Venue: TestLevel_Tom (walls, doors, a corridor), besides the sandbox, which has no geometry.
  The copy was taken there with `down` / `up --level GustavX:TestLevel_Tom` and back the same way.
- Tests (`tests/aiming/blocking/`):
  - `level_walls_and_doors` - behind the level's wall and its closed door a target action is
    refused "out of sight" (no hit chance) and a projectile "path blocked" (the hit chance still
    shown); the door opened, both are valid. The door is opened by a cheat and closed again by
    `GK.Stage.OnClean`.
  - `placed_objects` - five objects created one at a time between the host and a goblin; the
    projectile is refused exactly when the object's AI-grid tile is `ShootBlockItem` (every
    template that is not `CanShootThrough`); the target action never; 3.5 m aside the projectile
    is valid again. A composite of five cases, about 30 s.
- `Client/Aim.lua`:
  - The subscription to the game's picking is made from the bridge's tick on the main thread
    (`GK.Attention.Pollers.aimFollow`) once a level runs, kept when it fires, made again when it
    does not or the level changes, never dropped. Before, `hook()` subscribed from the deferred
    (render-thread) context the first time `Aim` was called - the hazard the attacks executor
    reported. A first move of it to load time failed: made at game start the subscription
    returned id 0 and never fired (every aim after a fresh `up` lost its target); the tick is
    where it now lives.
  - The relation channels (`GK_Ask`, `GK_Answer`) are made at load, not from a deferred call.
  - `Aim` waits for the reason of an invalid aim: at speed 8 the view model said invalid before
    the task wrote "out of sight", and `Aim` answered with no reason.
- Contract: lua-api.md section 11 (Aim's row, the subscription paragraph).
- Study: `research/studies/aiming.md`, rung 2.

**Proven.** In TestLevel_Tom, one session: `test tests/aiming/blocking --speed working` (run
`...-044636`, 2 passed), `--speed 1` (`...-044727`, 2 passed), `--speed working` again
(`...-044840`, passed); the scene cleaned and verified before each test and after each suite,
the door closed again. Back in the sandbox after a fresh `up`: `tests/aiming/cursor` and `area`
at `--speed 1` and `--speed working`, all passed.

**Surprises listed by the runner.** None, at either speed, for the four aiming tests run.

**Findings.**

- finding: a projectile's aim is refused when the AI grid's tiles on its line carry `ShootBlock`
  or `ShootBlockItem`; an item marks `ShootBlockItem` when its template is not `CanShootThrough`.
  Evidence: `placed_objects`, `level_walls_and_doors`, `Ext.Level.GetTileDebugInfo`.
- finding: a target action's sight is blocked by the level's walls and closed doors and by no
  object created at run time, a closed door of the same template included. Evidence: the same
  two tests.
- finding: `Osi.CanSee`, the target action's sight and the projectile's path are three different
  answers; the physics scene (raycasts) is a fourth that none of them follows exactly (an
  invisible blocker of the level is hit and refuses nothing).
- finding: a placed object marks its tile a moment after it is made; an aim before that is
  valid. No event of the mark is known; the test reads the tile.
- finding: the cursor keeps the hit chance for a blocked path and shows none for a target out of
  sight.
- finding: an incapacitating status (`PARALYZED`, `SLEEPING`) given in the chunk that creates a
  creature hides it from the party until it moves (`IsInvisible` 1, `InvisibilityComponent`);
  `BLESS` does not; a later chunk does not. Not `GK.Stage.Spawn`'s: the raw calls' order.
- bug (ours, suspected): `GK.Stage.Verify` does not see a level door left open (state of a level
  object); the test closes it through `OnClean`. For the stage's owner.
- bug (ours, fixed here): `GK.Gui.Aim` after a fresh game start could not hold an aim if the
  subscription had been made too early; and it subscribed from the render thread.
- open: the ECS reads of `Aim`, `Aiming`, `Confirm` still run on the render thread (Ext.UI.Defer).
  Not moved: the hazard was not shown here (no disagreement seen in this rung's aims) and the
  move is not small (every read of the targeting and the view model). Recorded as a suspected
  bug of ours, with the attacks executor's one failure as its evidence.

**Where time went.** The edit tool lost its write access to the worktree after the coordinator's
first message and needed the files read again. Exploring the sandbox for blocking (a flat level
with nothing to block) before moving to TestLevel_Tom. Objects that `RequestDeleteTemporary`
does not remove (the indestructible statue and rack) stayed and spoiled one series of readings;
`GK.Stage.Remove` and `Clean` take them. An unseen order effect: aims made before an object's
tile is marked.

**Crashes, hangs.** None.

**Wishes and to-dos.**

- To the coordinator (stage): Verify to see a level object's state (a door opened, a lever
  pulled) as it sees positions.
- To-do (aiming, rung 6): what holds a target action's sight - the tracer over an aim refused
  "out of sight" through the level's door, against the same aim through a created door.
- To-do (aiming, rung 3): high ground; the flight of a projectile against the cursor's verdict.
- To-do: an event for an object's mark on the AI grid.

**API review.**

- Added nothing new to the API; `Aim` waits for the whole verdict; the picking is followed from
  the shared tick (one poller, a cheap check every 500 ms once confirmed).
- Wanted, an observation verb: the AI grid's tile at a point (`GK.See.Tile(point)` or a field of
  `Aiming`: which tile on the line refuses the path). The tests read `Ext.Level.GetTileDebugInfo`
  themselves; a third test that needs it makes it a helper.
- Wanted: `Aiming` to name which kind of refusal it is (`blocked = "sight" | "path"`) rather than
  the localised reason - the tests compare two reasons for being different, which is weak.

## Rung 3: three kinds of reach (continuation of the executor)

**Asked.** Melee reach, a ranged attack in a straight line, a thrown or lobbed one over an
obstacle, each its own rule; with rung 2's question: the projectile's flight against the cursor.

**Done.**

- Tests (`tests/aiming/reach/`, sandbox):
  - `melee_reach` - a melee attack reaches `WeaponRange` + the attacker's and the target's `Hit`
    radius, centre to centre; dagger and glaive, goblin and ogre, each just inside (taken) and
    just outside (refused "not enough movement", the host's movement spent).
  - `ranged_range` - the shortbow's normal range is its `WeaponRange`; up to that plus the two
    hit radii the shot is taken with one disadvantage; beyond, refused; Fire Bolt reaches as far
    with no penalty.
- `Client/Aim.lua`: a new aim always waits the settle (600 ms and ten frames of the picking);
  before, it answered as soon as the view model moved on. The task line's context byte 0x04 is
  read as `Warning`.
- Study: `research/studies/aiming.md`, rung 3.

**Proven.** In the sandbox, one session: `test tests/aiming/reach` at `--speed 1`, `working`,
`1`, `working`, all passed (runs `20261115-054556-suite` to `-054849-suite`); after the last
change to `Aim`, every sandbox test of `tests/aiming` (cursor, area, reach) at `--speed 1` and
`--speed working`, all passed (`20261115-055327-suite`, `-055515-suite`). The rung-2 tests of
`tests/aiming/blocking` (TestLevel_Tom) were not run again after the changes to `Aim`.

**Not done, and why.** The third kind - a throw or lob over an obstacle that refuses a straight
shot - and the flight against the cursor. Fire Bolt's verdict past a placed barrel changed within
one session with nothing changed that I could find (tiles marked, physics rays still hitting the
barrels): first refused where the barrel stood in the goblin's half of the line, later taken
everywhere, the 2 m wine barrel too. A throw over that barrel was refused "path blocked" in the
first phase. A test of "throw over what a bolt cannot pass" needs the straight verdict to be
reproducible first. See the study, "The straight path against the cursor".

**Findings.**

- finding: melee reach = `WeaponRange` + `Hit` radius of attacker + `Hit` radius of target
  (`Bound.AIBounds.Hit.Radius`: 0.2 medium and small creatures, 0.7 an ogre). Evidence:
  `melee_reach`, sweeps by 0.02 m.
- finding: a ranged weapon's long range is exactly the two hit radii beyond its normal range
  (the wiki: "not measured, not more than 5%"); a spell has the same reach and no penalty.
  Evidence: `ranged_range`. A curiosity worth telling players: the long range is measured
  centre to centre plus the creatures' sizes, so it is longer against an ogre.
- finding: with no movement left, reach is refused as "not enough movement"; the game computes
  the walk (`MoveInSpellRangeCache`) and refuses it; the edge has a few centimetres of slack.
- finding: Throw takes two clicks (the object, then where); the first leaves the targeting open.
- bug (ours, fixed): `Aim` answered before the cursor's verdict about walking into reach was
  made - Fire Bolt 0.15 m beyond reach read valid at 200 ms and refused after the settle. Now a
  new aim waits the settle. Evidence: `ranged_range` failed three runs in four before, passed
  four of four after.
- bug (ours, suspected, now seen here): `Aim` answered "no character of this client is
  controlled" 124 ms into an aim at the working speed (`melee_reach` after `cursor/refused_target`,
  run `20261115-055201-suite`) - the render-thread read of the ECS. `Aim` now reads again rather
  than answer that while the aim is held; the reads stay on the render thread.
- open (rung 2 weakened): the projectile's "path blocked" is not decided by the AI grid tile
  alone; height and something not found enter.
- bug (ours, suspected): `GK.Stage.Outfit` is not undone by the clean: a two-handed glaive given
  to the host took its dagger out of hand, the clean removed the glaive and left the host with
  no weapon (Verify named it: `Target_MainHandAttack` lost). The test puts the weapon back with
  `OnClean`. For the stage's owner.

**Where time went.** The straight-path readings in the sandbox, which changed under me and are
not explained; a probe scene where my own objects overlapped the host. Four runs to find that
`Aim` answered early (the diagnosis read the task's `MoveInSpellRangeCache`, `PathDistance` and
`CanReachTarget` from inside the test). The edit tool refused this worktree until I entered it.

**Crashes, hangs.** None.

**Surprises listed by the runner.** `combat_started` in both reach tests: real, each starts a
fight on purpose; allowed in their headers.

**API review.**

- Slower: `Aim` now always waits 600 ms for a new aim (the reach tests take about 20 s each).
  The event of the verdict is the gap; leads: `CurrentPlayer.CurrentTarget`, the task's
  `MoveInSpellRangeCache`.
- `Confirm` on Throw's first click answers ok = false "still open: the game did not take the
  click", which is wrong for a two-click action: it should answer that the object was picked
  (the task's `Targets` grew). To do in this file, with a test of Throw.
- `blocked = "sight" | "path"` not built: the refusals seen carry the same context byte (4) for
  "not enough movement" and "path blocked"; telling them from the game's state and not the text
  is not found yet.
- Wanted still: an observation of the AI-grid tile; not needed by the tests of this rung.

**Announced change for the attacks executor.** `GK.Gui.Aim`:

| Case | Old answer | New answer |
|---|---|---|
| a new aim whose view model changed at once | answered then (about 200 ms) | answered after the settle (600 ms, ten frames), with the verdict on walking into reach made |
| a line of the task with context byte 4 | `context` the view model's name for it | `context = "Warning"` |
| the same aim made again | at once | unchanged |

## Rung 3, continued: the path verdict settled (second continuation)

**Asked.** Rung 3 not closed as two of three: rung 2's rule ("a projectile is refused exactly
when a tile on the line is `ShootBlockItem`") was seen not to hold. Investigate in order - the
draw rate (the deck was closed in the night), rung 2's tests again in TestLevel_Tom, the tracer
over refused and taken - then restate the rule or mark it not established.

**Done, and how it was shown** (probes run as throwaway tests from a scratch folder, not kept;
their readings are in the study, rung 3, "The straight path against the cursor"):

1. Draw rate: `gatekit window fps aim 10` (no deck needed), then 30 and 4. The verdicts past a
   barrel, a wine barrel and in the clear, read every 0.5 s for 18 s: the same at every rate, and
   all taken out of a fight. Not the cause. The picking updates `Aim` counts as "frames" are not
   the frames drawn (more of them per second at 4 a second than at 30).
2. The fight: out of a fight every aim past the barrels was taken; in a fight with movement left,
   taken; with none, refused "not enough movement" with the barrel in the goblin's half. Out of a
   fight the click walked the host 7.2 m round the wine barrel before the bolt. The task's
   `MoveInSpellRangeCache.Solution` holds the place it walks to.
3. Rung 2 again in TestLevel_Tom (fresh `up`): `level_walls_and_doors` passed; `placed_objects`
   failed - every object's projectile aim taken out of a fight. A tick-by-tick record of the task
   from the aim: "path blocked" for the first three or four picking updates (about 20 ms), then the
   walk is judged (valid, or "not enough movement" in a fight with none). Stable over a minute of
   holding, at speed 1 and 8. Rung 2's `Aim` answered at the view model's first change and caught
   the first verdict; since the settle, the same aims read taken. That is the "refused early,
   taken later".
4. The tracer (step 3 of the order): not run. The verdict is explained by the task's own fields
   read every tick; which component differs between "refused" and "taken" is the walk's, and the
   tracer stays with rung 6 for what decides the straight check itself.

**The rule restated.** The cursor's verdict on an aim past an obstacle is the walk's: the line
from where the caster stands, and where it is blocked a place to act from and the walk there,
taken when it can be walked, refused "not enough movement" when not, refused with the line's
reason only when no place is found (the level's wall and door, out of a fight). Rewritten
`tests/aiming/blocking/placed_objects`: a barrel out of a fight (taken), five objects in a fight
with no movement (a refusal past a placed object always gives the one walk's reason; a
`CanShootThrough` object blocks neither kind; a step aside gives the aim back), then movement
given back and the click: the host walks 1 m round the barrel before the bolt strikes.
`level_walls_and_doors` unchanged but for its comment. **Not established**, said so in the
test's comment and the study: which other objects block which kind - the barrel the projectile
only, the wine barrel both, the created closed door sight only; neither the grid's tile nor the
physics rays tell it.

**Proven.** TestLevel_Tom, one session: `test tests/aiming/blocking --speed working` (run
`20261115-063441-suite`, 2 passed), `--speed 1` (`20261115-063553-suite`, 2 passed); the scene
cleaned and verified before each test and after each suite.

**Surprises listed by the runner.** None. `combat_started` is allowed in `placed_objects`' header:
real, the test starts a fight.

**Findings.**

- finding: the cursor judges the walk to a place with a clear line, for a projectile and for a
  target action alike; out of a fight any aim it can mend by walking is taken. Evidence:
  `placed_objects`, the probes above.
- finding: the task's first verdict past an obstacle is the straight line's and lasts three or
  four picking updates. Evidence: the tick record.
- finding (corrects rung 2): runtime objects do block sight - with no movement the wine barrel and
  a created door refuse Chill Touch; rung 2 saw none because the walk mended it.
- finding: in a fight the game starts no fight with an immortal enemy (`GK.Stage.Fight` with the
  goblin made immortal first: never in combat); the test makes it immortal after the fight began.
- finding: `game_turn` with a `who` did not come for a fight started by `GK.Stage.Fight` after
  other aims in this test (the turn order showed the host active); the test reads
  `TurnBased.IsActiveCombatTurn`. Not chased: for the event log's owner if it recurs.
- finding: `Osi.RestoreParty` does not give back movement in a fight; the test sets the
  resources' `Amount` to `MaxAmount`.
- bug (ours, fixed in the test): rung 2's `placed_objects` asserted a transient.

**Where time went.** A test rewrite that met three things about fights (immortal enemy, the turn
row, movement not restored), one run each.

**API review.** `Aim`'s settle stays: what the game waits for is the walk's search, and no field
says in every case that it is done (`MoveInSpellRangeCache.field_0`, `PathDistance` stale). A
wish, observation: `Aiming().walk` - the distance and place the game would walk before acting -
once a field is found that is not stale; it would let a test tell "taken with a walk" from
"taken where it stands" without a click.

## Rung 3, the third kind of reach: a throw over an obstacle

**Asked.** A thrown or lobbed attack over an obstacle, in a fight with the movement spent;
`Confirm`'s two-click Throw in `Aim.lua`, announced. Also: the resource write of `placed_objects`
through the ledger; the "frames" of `Aim`'s gap reworded; the weapon `OnClean` of `melee_reach`
removed if the clean now gives the weapon back.

**Done.**

- `Client/Aim.lua`: `Confirm` answers ok with `picked = uuid` when the click is the first of a
  two-click action. The sign is the task's `Flags` gaining `NeedsPreviewToActivate` with the
  object in `Result.Target`. `Result.Target` alone is no sign: the task holds the hovered object
  there before any click, and `Targets` does not grow.
- `tests/aiming/reach/throw_over` (TestLevel_Tom): in a fight with no movement, a goblin 6 m east,
  a dagger like the host's at its feet. A 1.3 m barrel half way: Fire Bolt refused (the walk's
  reason), Throw taken. A 2 m wine barrel near the thrower (1.5 m) or near the goblin (4.5 m):
  Throw refused "path blocked". The wine barrel half way: Fire Bolt refused, Throw taken, and the
  dagger thrown lands 0.3 m from the goblin and hits it while the host stays where it stood.
- `placed_objects`: the resources written back through `GK.Stage.Track` (the clean restores the
  amounts from before; the host's movement read 9 of 9 after the suite).
- `melee_reach`: the weapon `OnClean` removed; the clean gives the dagger back (shown by the
  sandbox runs below).
- `Aim.lua` comment and lua-api.md: the settle counts updates of the picking system, not the
  frames drawn; the contract and `lua-types` give `Confirm`'s `picked`.

**Proven.** TestLevel_Tom, one session: `test tests/aiming --speed working` (run
`20261115-065116-suite`: `level_walls_and_doors`, `placed_objects`, `throw_over` passed; the
sandbox tests not run, another level) and `--speed 1` (`20261115-065259-suite`, the same three
passed). The sandbox, after `up --level GustavX:Basic_Level_A`: `test tests/aiming --speed 1`
(`20261115-065615-suite`) and `--speed working` (`20261115-065758-suite`): `preview_is_the_blast`,
`hit_chance`, `refused_target`, `melee_reach`, `ranged_range` passed at both; with the weapon
`OnClean` gone, `melee_reach`'s clean left the host's dagger in hand (Verify clean, the tests
after it pass). Surprises: none; "kept by the game" lines only (the runner's own notes).

**Findings.**

- finding: a throw is judged by its arc: over a low barrel anywhere on a 6 m line, over a tall
  wine barrel only in the middle (taken at 2.5 and 3.5 m, refused at 1.5, 4.5, 5.2 m). A probe
  over ten places; the test keeps four.
- finding: a throw with its object picked is refused "path blocked" - it looks for no place to
  walk to, unlike a spell.
- finding: the flight agrees with the cursor for the one throw made.
- finding: Throw's first click: `NeedsPreviewToActivate` added to the task's flags; the task's
  `Target` loses the entity; the cast's targets hold only where it goes.
- `game_turn`: the reach tests in the sandbox wait for it after `GK.Stage.Fight` and get it (see
  the runs below); in TestLevel_Tom the two tests of mine that need the host's turn read
  `TurnBased.IsActiveCombatTurn` and do not use the row, so it was not seen missing again.

**Announced change (`GK.Gui.Confirm`).**

| Case | Old answer | New answer |
|---|---|---|
| the first click of Throw (the object picked, the targeting open) | ok = false, saw "the targeting of Throw_Throw is still open: the game did not take the click", after 2.5 s | ok = true, `picked = <uuid of the object>`, saw "the first click of Throw_Throw picked <name>: the targeting waits for the second", at once; the aim stays held |
| every other case | unchanged | unchanged |

**Wishes and to-dos.** A lobbed spell besides Throw, and the arc's height as a number, not
measured. Throw's range by weight: not measured (the dagger at 6 m was in range).

**API review.** `Confirm` now covers Throw; Jump (two clicks too?) not checked. The tests read
`TurnBased.IsActiveCombatTurn` raw; a fair observation of whose turn it is would serve them (the
stage's `GK.Stage.Combat().active` is the same read, in a cheat namespace).

## Rung 4: the area's preview against the blast

**Asked.** Who the preview says an area touches against who it touches; when they differ (a
preview once left out an ally inside the blast that then died). The rule first: the wiki on areas,
what "touches" means for a creature's bounds, what players report.

**Done.**

- Sources: the wiki gives radii, not what "within" means for a creature's size, and nothing on
  previews that lie; the first hypothesis was rung 3's hit radius.
- Probes (throwaway tests in a scratch folder): a ring of goblins and ogres at 3.5-5.2 m from
  Fireball's point, then 0.05 m either side of the edge; creatures unseen by the party (paralysed in
  the creating chunk; `INVISIBILITY`); a goblin on the flight line, seen and invisible.
- Tests (`tests/aiming/area/`, sandbox):
  - `edge_of_the_blast` - a creature is touched when its centre is within the radius plus its hit
    radius; goblin and ogre each side of the edge; preview and blast agree.
  - `preview_shows_the_seen` - the preview outlines only the seen; an invisible goblin and an
    invisible ally inside burn without being outlined.
  - `unseen_on_the_path` - an invisible goblin on the flight catches the bolt; the blast goes off
    there, burning it and one beside it, and spares the goblin the preview outlined.
  - `preview_is_the_blast`: its comment now explains rung 1's ally at 3.9 m (unseen, not an edge).

**Proven.** In the sandbox, one session: `test tests/aiming --speed working`
(`20261115-071222-suite`) and `--speed 1` (`20261115-071412-suite`): the four area tests, the two
cursor tests and the two sandbox reach tests passed at both, the scene cleaned and verified before
each; the three TestLevel_Tom tests not run (another level).

**Findings.**

- finding: a round area's edge for a creature is the radius plus its `Hit` radius, centre to centre
  on the ground (the same edge as reach).
- finding: the preview outlines only creatures the party sees; the blast takes the unseen too.
  Rung 1's "left out ally" was an ally unseen because it was paralysed in the chunk that made it.
- finding: an unseen creature on Fireball's flight catches it; a seen one does not.
- finding (not followed): `Osi.CanSee` read 0 for a goblin behind an invisible one on a diagonal.
- A first design made two casts in one test (seen line, then invisible line) with the resources
  filled through the ledger on the host's turn; the second line was diagonal and the goblin behind
  the invisible one read unseen, so the test keeps one cast and the seen case stays a probe.

**Where time went.** The flight-line case: a layout that put the invisible goblin on the line by
accident showed it, and two layouts were needed to separate it from the area rule.

**Surprises.** `combat_started` in the three new tests: real, the blast starts a fight; allowed in
their headers.

**API review.** Nothing added. `GK.Gui.Aiming().affected` is the game's outline and so carries the
party's sight: a runner choosing an aim by it (`GK.Cheat.AimBest`, rung 5) is blind to the unseen,
as a player is. Fair: the turn observation wish was not needed again in this rung.

## Rung 5, cut to its core: whom the party perceives

**Asked.** A fair `GK.Gui.AimBest` over the creatures the party perceives, with the observation of
whom it perceives. Cut by the coordinator mid-rung (the owner's change of course): the observation
only, tried on two unlike cases; `AimBest` left for later; the night goes to the instruments.

**Done.**

- `GK.See.Perception{target, who, radius, dead}` in `Server/Situation.lua` (the one addition
  allowed there), its registry line, the contract (lua-api.md section 11) and `lua-types`. It reads
  the game's two states: the `Invisibility` component (what hides a creature from other parties'
  clients) and `ServerViewshedParticipant.CanSee` on the creature (whose sight holds it). It lists
  only the shown; a hidden creature is never named, placed or counted.
- Study: rung 5 in `research/studies/aiming.md` (what the game keeps, the addresses read).

**Tried, live (probes by `exec`, not tests), with what it answered:**

| Case | `perceived` | `sight` | `Osi.CanSee` | preview |
|---|---|---|---|---|
| goblin 6 m, plain (sandbox) | true | the host | 1 | - |
| goblin 6 m, `INVISIBILITY` (sandbox) | false (absent from the list) | - | 0 | not outlined (rung 4) |
| goblin 18.9 and 19.5 m, beyond the host's 18.5 m sight (sandbox) | true | none | 0 | outlined |
| goblin behind the wall and behind the closed door (TestLevel_Tom) | true | none | 0 | - |
| goblin in the corridor, in the open (TestLevel_Tom) | true | the host | 1 | - |
| a uuid that is not there | false | - | - | - |

**Not done, and why.** `GK.Gui.AimBest` (the coordinator's cut); no test written - the rule the
brief named is AimBest's, and the observation's cases above are probes. A sneaking NPC could not
be staged hidden (`SNEAKING` gave no `Invisibility`).

**Findings.**

- finding: invisibility is one global component, not per observer; the per-observer state is the
  sight system's set kept on the one seen. The preview follows the first: it outlines creatures
  beyond every member's sight. So "perceived" for a player is "not hidden", and sight is narrower.
- finding: Fireball aimed 3 m from a goblin on the other side of TestLevel_Tom's wall outlined
  nothing (valid aim). Whether the blast stops at the wall: not checked.
- bug (`Situation.lua`, suspected, not mine to fix): `GK.See.Around` lists the sandbox's waiting
  hirelings as neutral characters 3-6 m from the host (`onLevel` passes a character with no
  level; `Osi.CanSee` answers 1 for them).
- A hostile goblin spawned in the host's sight in TestLevel_Tom started a fight by itself.

**Where time went.** Reading what `ServerViewshedParticipant.CanSee` means (it is "seen by", kept
on the seen; the first reading took it for "sees").

**Crashes, hangs.** None. One `down` / `up --level GustavX:TestLevel_Tom` on purpose. Before it,
`GK.Stage.Verify` answered not clean after my probe's Clean in the sandbox (not looked into: the
process was ended); after the fresh `up`, "no baseline" until `GK.Stage.Begin`.

**API review.** Added `GK.See.Perception`; `GK.See.PartyPerceives` (Osi.CanSee plus a ray of our
own) answers a third thing, close to neither; a candidate for removal once its callers move.

## Rung 6, first pass: every instrument once, on "where the party's sight is kept"

**Asked** (the owner's change of course): each instrument tried, in order, on one question, every
problem written down as it happens; then the questions of the descent.

**The question taken:** what writes `ServerViewshedParticipant.CanSee` (who sees a creature) and
what changes when a creature becomes unseen. Copy `aim`, TestLevel_Tom, one goblin, three other
games running beside it the whole time.

### The memory instrument (`GK.Memory`) - works; "who writes this field" is built

The documents disagree: `docs/writing-tests.md` and lua-api.md section 16 describe `Watch` and
`Hits`; the open items speak of it as wanted. **Built and working**: a hardware write watch that
names the writing instruction and a call stack, in this copy's fork build.

| Call | Answer | Expected | Time |
|---|---|---|---|
| `GK.Memory.Available()` | true | true | at once |
| `GK.Memory.Field(g, "eoc::InvisibilityComponent", "field_0")` | `{Address, Offset 0, Type uint8, Size 1, Scalar}` | the same | first call |
| the same with `"InvisibilityComponent"` (the Lua type name) | nil, no error | an error naming the engine name | - |
| `Field(g, ..., "field_4")` (a vec3) | `{Address, Offset 4, Type vec3}` - **no `Size`** | a size of 12, or a scalar's 4 | - |
| `GK.Memory.Watch(thatField)` | **raw error** from `Ext.Debug.WatchWrite`: "bad argument #2 (number expected, got nil)" | refused with a reason, or the first 4 bytes | - |
| `Watch(field.Address, 4)` | slot 2 | a slot | - |
| `Field(g, "esv::sight::ViewshedParticipantComponent", "CanSee")` | `{Address, Offset 16, Type Set<EntityHandle>}` | the same | - |
| four `Watch(address + 8i, 8)` over the set, goblin moved behind the wall, then back | two hits: `bg3_dx11.exe+0x7821AE` and `+0x7200D7`, thread, frame, a six-deep return stack | the sight system's write | 2 s each |
| `GK.Memory.Freeze(addr, "uint8", v)`, `Frozen()`, `Unfreeze()` | slot 1, listed, gone | the same | at once; a hold of the value it had, so not shown to override a game write |
| on the client: `Ext.Debug.GetFieldAddress`, `ReadValue` | an address and the value 2 | - | `GK.Memory` itself is server-only (`Server/Memory.lua`); the client is reached only through raw `Ext.Debug` |

Problems, as met:
- **A watch is lost when the entity's set of components changes.** Turning the goblin invisible
  adds `Invisibility` and `Stealth`, and the ECS moves all its components to new storage: the
  set's address changed (…165264 to …809680), the armed watch then sat on freed memory and
  caught **unrelated writes** - `bg3_dx11.exe+0x13CDA68` and `Noesis::StoredValue::CreateComplex`
  (`+0x46F5B50`, the UI library) - which read like real hits. Nothing in the answer says the
  component moved. Wish (toolkit): `Hits` (or `Poll`) checks that the watched field's component
  still lives at the address and says so.
- `Before` equals `After` in every hit (1 -> 1 at the remove; 1 -> 4294967297 at the add was the
  only change): what `Before` holds is not clear.
- `Hits` drains: an answer cut by my own `head` lost the hits for good. Not a defect; a care.

### `gatekit decomp at` and `decomp code` - work, about 0.8 s an address

| Address | `decomp at` names | My reading |
|---|---|---|
| `bg3_dx11.exe+0x7821AE` | `ls::HashTable<EntityHandle>::Remove` (fn +0x782110) | the set's removal |
| `bg3_dx11.exe+0x7200D7` | `ls::HashTable<EntityHandle>::Ensure` (fn +0x720010) | the set's insertion |
| `+0x2E5E784`, `+0x2E5E73B` (the caller of both) | `FUN_142e5dbc0` | a function of the sight system that adds and removes `ViewshedParticipantComponent` and fills each one's `CanSee` from the viewsheds, and raises `ViewshedParticipantsAddedEventOneFrameComponent` |
| `+0x2E53BAC` (its caller) | `FUN_142e53780`, typed: `(esv::sight::ViewshedSystem*, WorldView<... InvisibilityComponent, CanSense, cover::IsVisionBlocker, sight::Data, Transform ...>)` | the viewshed system's main pass; its world view is what sight depends on |
| `+0x239AE0C` | `SystemUpdate<esv::sight::ViewshedSystem>` | the system's update |
| `+0x13CDA68`, `+0x46F5B6E` | `FUN_1413cda40`; `Noesis::StoredValue::CreateComplex` | the stale watch's noise (above) |

`decomp code` read the functions above; nothing of it is copied here. `decomp grep
"AddComponent<eoc::InvisibilityComponent>"` took 56 s and matched by words, not the exact name
(it listed systems that only read the component) - the writer of `Invisibility` is not found yet.

### The tracer (`Ext.Entity.SetupTracing` / `EnableTracing` / `GetTrace` / `ClearTrace`) - works

- `SetupTracing()` with no argument: "Param #1 missing"; its options are declared nowhere I could
  find (no type of them in `Ext.Types`); `SetupTracing({})` is accepted. `GetTrace()` is an
  `ecs::ECSChangeLog` (`Frame`, `Flush`, `Entities` -> `ECSEntityLog` -> `Components` with
  `Create/Destroy/Modify/Replicate/OneFrame`).
- `gatekit test --trace`, which `docs/writing-tests.md` describes, **does not
  exist** (`test --help`); the runner has no tracer call. The open items say "not yet built" -
  that one is true.
- Over the goblin turning invisible, one second, against a baseline second (nothing on the two
  entities): the goblin gains `InvisibilityComponent` and `StealthComponent` (created),
  `CanSenseComponent` replicated with `capability::CanSenseChangedOneFrameComponent`; the host's
  `sight::EntityViewshedComponent` is replicated and `esv::sight::EntityViewshedContentsChanged
  EventOneFrameComponent` made on it - the host's viewshed lost the goblin. With `{}` no
  modification of `ViewshedParticipant` is listed (modifications need options I do not know).
  The aims refused and taken: not traced yet.

### Names - `names suggest`, `set`, `extender` work

- `names suggest eoc::InvisibilityComponent`: three fields at Script Extender's `field_0/4/10`.
- `names set esv::sight::ViewshedParticipantComponent 0x10 CanSee --source inferred` with the watch
  and the call chain as evidence: written (`research/coverage/layouts/extender/esv.sight.
  ViewshedParticipantComponent.json`, rung extender -> inferred, the name unchanged). Meaning
  checked: the set is "those whose sight holds this entity", kept on the seen one.
- Not set: `eoc::InvisibilityComponent+0x4` as `Position` (a dry run accepts it): the server's
  value is the creature's place when it turned invisible and a teleport does not write it, but
  I have not read the code that writes it, so the name would be a plausible one.
- `names extender --ns esv::sight | eoc::sight | eoc::Invisibility | eoc::Stealth`: nothing to
  raise. Script Extender's `CanSee` is now confirmed by the instrument.

- `decomp name` (taken from the working branch): `bg3_dx11.exe+0x2E5DBC0` named
  `esv::sight::ViewshedSystem::UpdateViewshedParticipants` and `+0x2E53780`
  `esv::sight::ViewshedSystem::ProcessViewsheds`, both inferred, each with the watch's hits and the
  return stack as evidence (`research/coverage/functions.jsonl`). It took an address inside the
  function as the watch reports it; `decomp at` then shows the name, the sentence and the evidence
  below the decompiler's own `FUN_` line. Worked at the first call, under a second each.
- `names set` and `decomp name` write the shared coverage record; done on the coordinator's
  instruction.

### Other copies running

Nothing misbehaved that I could tie to them: every call answered in the time above.

### Not yet, and why

- The questions of the descent (where an aim is refused; which placed object blocks which line
  and what writes an object's tile a moment after creation; why the level's door differs from a
  door placed at run time; why an unseen creature on the flight stops a bolt): not started - the
  night's time went to trying each instrument once. The first step proposed for the placed
  objects: the tile is not a component, so the watch needs the AI grid's address
  (`Ext.Level.GetTileDebugInfo` gives the flags, not where they live) - a wish for
  `GK.Memory` to reach the AI grid's tile, or `Ext.Debug.GetSystemAddress` for the grid's owner.
- What writes `Invisibility` (the add): `decomp grep` was too broad; next, the tracer with
  options from the fork's `Lua/Libs/Entity.inl`, or a watch on the status's own component.

## Run of the night's second half: the regression of `Confirm`, then the descent

### Task 1: `Confirm` took an ordinary attack for the first click of Throw

**Reproduced** in `aim` (sandbox): `test tests/combat/melee --speed 1` - `attack_by_die` failed
with `GK.Act.Attack` answering "clicked and left the targeting", `saw` = "the first click of
Target_MainHandAttack picked <the goblin>: the targeting waits for the second" (run
`20261115-080317-suite`). The attack itself was carried out (the coordinator's copy: hit points
lost, the action spent): only `Confirm`'s answer was wrong.

**The reading at the click** (a temporary record of the task on each look of `Confirm`,
Target_MainHandAttack on a goblin): 66 ms after the press the task's `Flags` hold no
`NeedsPreviewToActivate`; at 100 ms they gain it, with the goblin in `Result.Target` **and still
in the task's own `Target`**. Throw's first click (rung 3) drops the object from `Target`. So the
flag is not a sign of Throw alone; the flag with the entity gone from `Target` is.

**Fix** (`Client/Aim.lua`, `pickedOf`): `picked` only when the flag is there and the task's
`Target` no longer holds the entity of `Result.Target`. The bridge's rule and `Client/Act.lua`
are not at fault.

**A second, unrelated failure met on the way:** `offhand/light_weapons` failed once at speed 1
with `UI.Aim` answering at once "no character of this client is controlled" (43 ms after the
hotbar action; passed when run alone). `Aim` now reads it again for 500 ms before that is the
answer.

| Case | Old answer | New answer |
|---|---|---|
| `Confirm` on a one-click attack on a creature | ok, `picked = <creature>`, no `cast` (at about 100 ms) | ok with `cast` once the server writes the targets (as before rung 3) |
| `Confirm`, first click of Throw | ok, `picked` | unchanged |
| `Aim` when no controlled character is read at the call | ok = false at once | the same error only if it lasts 500 ms; otherwise the aim goes on |

**Proven.** Sandbox, one session: `tests/combat --speed 1` 10/10 (`20261115-081329-suite`),
`--speed working` 10/10 (`20261115-081901-suite`); `tests/aiming --speed 1` 8/8 of the sandbox
(`20261115-082255-suite`). `tests/aiming --speed working` first gave 6/8
(`20261115-082525-suite`): `melee_reach`'s aim at the goblin outside read the targeting as
`MoveTo`, and the host was left with an orphaned cast (`IsCasting`, `SpellAnimationLifetime`) that
no clean removed - the same hang the attacks copy met twice (decision 96); `melee_reach` uses no
`Confirm`, so not the change above. After `down`/`up`: 8/8 (`20261115-083202-suite`).
TestLevel_Tom, fresh `up`: `level_walls_and_doors`, `placed_objects`, `throw_over` passed at
speed 1 (`20261115-083418-suite`) and working (`20261115-083610-suite`). `dotnet test`: 401 passed.
Commit 357069b; the working branch merged (6fb2e97).

### The doors: is the leaf across the line, and is it closed? (the owner's two points)

Probes in TestLevel_Tom (`exec`, a door made by `GK.Cheat.Spawn` 4.5 m east of the host, where
`placed_objects` puts it; the host-goblin line runs along +x at z = 47.25):

| Door | Rotation (quat) | Leaf (the `Hit` box of `Bound.AIBounds`, local) | Across the line? | State |
|---|---|---|---|---|
| placed (`DOOR_Single_Dungeon_Abbey_B`, as `placed_objects` made it) | identity | x -1.015..1.015, y 0..3.01, z -0.04..0.26 | **no: the leaf lies along the line**, the line runs inside its thickness (z 47.21..47.51) | closed: `Osi.IsOpened` 0, `ItemISDoorClosed` present |
| the level's (`S_DOOR_SingleDungeonAbbey3`, -7.31, 0, 48.5) | (0, 0.707, 0, 0.707): 90 degrees about y | the same box | yes: the leaf runs along z, across a line along x | closed, the same two readings |

- **The placed door of `placed_objects` stood along the line, not across it.** Every claim made of
  it so far ("a placed door blocks sight and not the projectile", "the level's door differs from
  a placed one") is withdrawn until the corrected run below.
- The stage's spawn takes no rotation (`GK.Stage.Spawn`, `GK.Cheat.Spawn`: `Osi.CreateAt` with a
  position only) - a gap of the shared stage. `Osi.ToTransform(door, x, y, z, 0, 90, 0)` turns it:
  the transform and `Bound.RotationQuat` read (0, 0.707, 0, 0.707), and the door's grid points
  turned with it (five tiles along y of the grid, x = 38, instead of along x).
- **Opening and closing a placed door by cheat changes its tile at the next tick.** A record of
  every tick (`Ext.Events.Tick`, a probe) at the door's tile: `Osi.Open` - tick 1 the flags
  `Door, DoorItemBlock, ShootBlockItem` gone and `ItemISDoorClosed` removed, tick 34
  `Osi.IsOpened` 1 (the animation's end). `Osi.Close` - tick 1 the three flags back and
  `IsOpened` 0, tick 33 `ItemISDoorClosed` back. A door marks no `WalkBlockItem`.

**The tests now read the geometry and the state.** `placed_objects`: every object's `Hit` box,
through its transform, against the host-goblin line seen from above (a `need`); what is said
not to block, and the door, must also cover 0.3-1.0 m of height; the door is turned 90 degrees
by `Osi.ToTransform`, its leaf square within 5 degrees, its state read before each aim (closed,
opened, closed again). `level_walls_and_doors`: the level's door read the same way (5.3 degrees
off square, the line over the leaf, 0-3.01 m), and closed again after the open.

The first run of the new check ended **unmet** at the bottle rack: its box hangs from 1.43 to
3.09 m (a rack on a wall), the chest-high line passes under it. It is printed now and no longer
asserted as "CanShootThrough blocks nothing". The crate's box (0-1 m) is crossed.

**The corrected run** (both doors across the line, their state read):

| Door | Closed | Opened | Closed again |
|---|---|---|---|
| the level's, out of a fight | projectile "path blocked", target "out of sight" | both valid | both refused, the same reasons |
| placed, in the fight with no movement | both "not enough movement" | both valid | both "not enough movement" |

So: **a closed door across the line blocks both kinds; opened, neither - the level's and a placed
one alike.** Withdrawn, in the test's comment and the study: "a placed door blocks sight only"
and "the level's door differs from a placed one" - both came from the door lying along the line.
Hypothesis "a placed door comes into the world open": not held (closed at once, both readings).

Runs (TestLevel_Tom, `test tests/aiming/blocking`): speed 1 `20261115-084751-suite`, working
`20261115-084933-suite`, both tests passed at both. Gap for the shared stage: `GK.Stage.Spawn` /
`GK.Cheat.Spawn` take no rotation.

### Task 2a: who writes the AI grid tile of a placed object - found and named

**Reaching the tile** (no instrument gives it; Script Extender's own layout in the fork's
`GameDefinitions/Ai.h` and `CoreLib/Base/LegacyMap.h`, read and followed by hand, a probe in the
scratchpad run by `exec @file`):

| Call | Answer | Expected |
|---|---|---|
| `GK.Memory.Field(door, "eoc::BoundComponent", "Bound")` | the `Bound` struct's address (the proxy dereferenced), type `Bound` | the same - first call |
| `GK.Memory.Read(bound + 0x18, "uint64")` | a pointer whose first word is `bg3_dx11.exe+0x5512FE8` (a VMT) | the grid (`Bound.AiGrid`, hidden from Lua) |
| `Read(grid + 0x68/0x6C/0x70)` | 14, 19, a table | `Subgrids` (ItemCount, HashSize, buckets) - the offset depended on the size of a hidden `Pool`: 0x68 was the first of the candidates and held |
| walk the buckets for `GetTileDebugInfo(p).SubgridId` | the subgrid (128 x 128) | the same |
| `GK.Memory.Element(subgrid.TileGrid + 0x10, x + y * Width, 16, 0)` (new tool) | the tile; its `MetaDataIndex` 32 = the door's, 0xFFFF on an empty tile | worked at the first call |

**The watch** (`GK.Memory.Watch(tile, 8)`, write):

- On `Osi.Open` of a placed door: four hits, two at frame 0 and two at frame 33: `+0xE38E62`
  (return chain `eoc::BoundComponent::Decrease` < `eoc::AiGrid::RemoveObject` <
  `esv::Item::UpdateBoundFlags` < `FUN_1442940c0`) and `+0xE3353B` (chain `FUN_140e38ae0` <
  `FUN_140fc53f0` < `eoc::AiGrid::ForEachBoxCell` < `eoc::BoundComponent::ForEachCell` <
  `FUN_140fc1cc0`, whose caller is `eoc::BoundComponent::Increase`). The value lost
  0x100 | 0xC00000 = ShootBlockItem, Door, DoorItemBlock (read back by `GetTileDebugInfo`).
- On a barrel created on an empty tile: seven hits over frames 103-106 of the two same
  instructions, alternating remove and add (the item's bound refreshed as its flags settle); the
  tile gained 0x180 = WalkBlockItem, ShootBlockItem.
- `Before` equals `After` in every hit (the known defect); `Moved` never set (the tile is no
  component). Other copies running: nothing seen.

**Names given** (`decomp name`, inferred, `decomp code` read for each):

| Address | Name | Our sentence |
|---|---|---|
| `bg3_dx11.exe+0xE38D70` | `eoc::AiGrid::RemoveObjectFromTile` | takes one bound object off one tile: finds the subgrid by the tile's id, clears the object's grid flags from the tile, drops the object from the tile's metadata |
| `bg3_dx11.exe+0xE38AE0` | `eoc::AiGrid::AddObjectToTile` | puts one bound object on one tile: finds or makes the tile's metadata, adds the object, counts it on the subgrid, rebuilds the tile's object flags |
| `bg3_dx11.exe+0xE33310` | `eoc::AiGrid::UpdateTileObjectFlags` | rebuilds the object part of one tile's flags from the objects in its metadata: all their flags off, then those of the objects whose height reaches the tile's floor band back on |

Their variables named with `decomp vars` (new tool: worked at the first call; `decomp code` then
listed the grid's named fields above the code). Fields with `decomp field` (new tool, first call):
`eoc::AiGrid` +0x18 `MetaData`, +0x68 `Subgrids`; `eoc::AiSubgrid` +0x18/+0x1C `SizeX/SizeY`,
+0x30 `TileGrid`; `eoc::AiGridTileData` +0x8 `Width`, +0x10 `Tiles`; `eoc::AiGridTile` +0x0
`Flags`, +0xC `MetaDataIndex`; `eoc::Bound` +0x18 `AiGrid` - source extender, each confirmed by
the reads above and by the named functions reading the same offsets.

**What in a placed object makes it block** - the object's `Bound.Flags` (written by
`esv::Item::UpdateBoundFlags`, on the chain) and its vision blocker, against what the cursor did
(`placed_objects`, the door corrected):

| Object | `Bound.Flags` | physics groups | `ServerIsVisionBlocker` | blocks (cursor) |
|---|---|---|---|---|
| barrel | ItemWalkThroughBlock, ItemShootThroughBlock | CanSeeThrough2 | no | projectile, not sight |
| wine barrel | ItemSeeThroughBlock, ItemWalkThroughBlock, ItemShootThroughBlock | - | yes | both |
| door, closed | ItemSeeThroughBlock, ItemShootThroughBlock, Interactable | - | yes | both; opened: flags none, blocks neither |
| crate | CanClimbOn, WalkOn | CanShootThrough, CanSeeThrough2 | no | neither |
| bottle rack | ItemWalkThroughBlock | CanShootThrough, CanSeeThrough2 | no | (the line passes under it) |

So the rung-3 puzzle has its answer: **`ItemShootThroughBlock` (absent with the physics group
`CanShootThrough`) blocks a projectile, and `ItemSeeThroughBlock` with a vision blocker (absent
with `CanSeeThrough2`) blocks sight**; the tile carries the first as `ShootBlockItem` and has no
flag for the second - sight goes by the vision blocker (`cover::IsVisionBlocker` is in the world
view of `esv::sight::ViewshedSystem::ProcessViewsheds`, rung 6). Not yet asserted in a test.

### Task 2b: where the cursor's verdict is decided (client)

Scene: TestLevel_Tom, Fire Bolt taught, a goblin 6 m east in the corridor (taken) and one behind
the south wall (refused "path blocked"), both paralysed. The hostile goblin in sight started a
fight by itself (seen before); the host kept its movement.

**The tracer** (client, `exec client`):

| Call | Answer | Expected |
|---|---|---|
| `SetupTracing({})`, `EnableTracing(true)`, a baseline second, the aim refused, the aim taken | each log keyed by component **type index**, not name (`33246 Create+OneFrame`) | names - mapped through `Ext.Debug.GetComponentTypes()` |
| the same with `{TrackModifications = true, TrackReplication = true, TrackECB = true}` (options read in the fork's `Lua/Libs/Entity.inl`), the baseline's kinds subtracted | refused and taken give the **same** rows (the caster's steering and rotation, the hovered surface, the cast entity's effects, sounds and `SyncTargeting`) **except** `ls::OutlineComponent` destroyed on the goblin left / created on the goblin aimed | a difference in the task's state |

So the verdict is in no component: the tracer cannot see it. It lives in the cursor's task.

**The field.** `ecl::CharacterTask_Spell.TaskInfo` (read from Lua): `Succeeded` true when taken,
false when refused; `Texts` one line (the hit chance) or two (the warning); `Error` 0 in both.
Its address on the client by raw `Ext.Debug` and Script Extender's layout (fork
`Controllers/Input.h`, `CharacterTask.h`): `Ext.Debug.GetFieldAddress(caster,
"ecl::ClientCharacterComponent", "InputController")` holds the controller's pointer (its first
word a VMT, `+0x5918678`); `PreviewTask` at controller +0x58 (VMT `+0x56A4CC0`); `TaskInfo` at
task +0x8, `Succeeded` at +0x1D - read 0 while refused and 1 taken, as Lua says.

**The watch** (client: raw `Ext.Debug.WatchWrite(task + 0x1D, 1)`, `WatchPoll`, `ReadWatchHits`,
`ClearWatch` - worked at the first call; `GK.Memory` is the server's only): over one aim, 238
hits in 54 frames - the verdict is rewritten every picking update - at five instructions, all
under `ecl::InputController::Update` (`+0x1B3F9F0`) < `+0x1B40730` <
`ecl::CharacterTask_Spell::UpdatePreview` (`+0x1FB5140`) < `ecl::CharacterTask_Spell::SetCursor`
(`+0x1FB5DF0`) < `ecl::CharacterTask_Spell::GetTaskInfo` (`+0x1FBABD0`, two writes) and two helpers
of theirs (`FUN_141f6be00`, `FUN_142006aa0`). Reading `GetTaskInfo`: the warning line is the text
of an error mask (`EocUIHelper::GetTaskErrorsTS`) made by `ecl::CharacterTask_Spell::GetError`
(`+0x1FB4340`: 0x1000 when a resource cost cannot be paid, 0x8000000 from the cast's own
validation) over `ecl::CharacterMoveTask::GetError` (`+0x205F810`: 2 when the task's
`CanReachTarget` (+0x13D) is false, 0x10000 when `IsPathLimited` (+0x13E), 0x40 and others).

A second watch on task +0x13C (4 bytes: `CanReachTarget`, `IsPathLimited`): rewritten every
frame by `FUN_142062a90` (from `UpdatePreview`), but **the same bytes** (`CanReachTarget` 1,
`IsPathLimited` 0) for the refused and the taken aim: "path blocked" behind the wall is not the
walk's bit.

**Named:** `bg3_dx11.exe+0x1B40730` `ecl::InputController::UpdatePreview` (chooses and sets the
preview task, has it update its preview; on every hit's chain). The other functions on the chain
already carry the symbol database's names.

**Not yet, and why:** which bit of the error mask is "path blocked" and which "not enough
movement", and who computes it: the mask is a return value (no memory to watch) and the client
has no `GK.Memory` (no `Field` for a non-component struct, no `Hits` with a return chain named
in one call, no clean). What the client side lacks: `GK.Memory` itself (the raw calls work, but
every probe re-does the address walk), a way to read a virtual function's return (the error
mask) or a breakpoint on a function's exit, and a tracer that sees a task (it sees components
only).

### Task 2c: an unseen creature on the flight line stops a bolt, a seen one does not

Two probe scenarios (scratchpad, run by `gatekit test <folder>`, the runner's clean between):
`unseen_on_the_path`'s scene (sandbox, Fireball at a point 12 m out, a goblin on the line at
7 m, one beside it, one past the point), the goblin on the line invisible in one and seen in the
other; the server's tracer (`SetupTracing({})`) on from just before the click to a second after
the first `game_damage`, summarised per entity with the component names.

| | seen | unseen |
|---|---|---|
| who died (the blast's place) | the goblin past the point | the goblin on the line and the one beside it |
| the cast's targets (`Confirm`'s `cast.targets`) | the point (15.75, 0.01, 2.25) | **the same point** |
| the goblin on the line, in the trace | `FleeCapability` replicated only | death's components, and `eoc::InvisibilityComponent`, `eoc::StealthComponent` **destroyed** (revealed by the hit) |
| every other entity (the projectile among them), by component and kind | 90 kinds | the same 89 (one `inventory::MemberTransform` replication fewer) |

So the two casts are the same request to the same point and the projectile's own entity shows the
same components; the only difference before the hit is the goblin's `Invisibility` and `Stealth`
components. The tracer shows the consequence (the blast's place), not the cause.

**The cause, found by the caster's place** (the same probes, the host's position read before the
click and after the cast; and a pair with Fire Bolt, a single-target projectile, at a goblin 12 m
out past one on the line at 7 m, out of a fight):

| Cast | goblin on the line | the host before casting | what is hit |
|---|---|---|---|
| Fire Bolt | seen | **steps 1.00 m aside** (z +1.00) | the target |
| Fire Bolt | invisible | does not move (0.00 m) | **the goblin on the line** |
| Fireball | seen | **steps 1.00 m aside** (z +1.00) | the blast at the point (the goblin past it dies) |
| Fireball | invisible | (as the bolt; the blast went off at the goblin on the line) | the goblin on the line and the one beside it |

So a seen creature does not let the bolt through: **the game counts a seen creature on the line
as an obstacle and walks the caster to a place with a clear line before the cast** (the walk of
rung 3, here one metre), while an unseen one is not counted, the caster casts from where it
stands and the flight stops at the first creature in its way. The same for a single-target
projectile and for Fireball. Who decides the step aside: the cursor's task (2b's chain), not
traced to its bit.

### Task 2d: Script Extender's declarations against what was read

`names extender --ns` for `eoc::BoundComponent`, `eoc::Bound`, `eoc::ItemISDoorClosed`,
`eoc::IsDoor`, `esv::IsVisionBlocker`, `esv::cover`, `eoc::InvisibilityComponent`,
`eoc::StealthComponent`, `ecl::ClientCharacter`, `ls::Physics`, `eoc::ShootThroughType`: nothing to
raise (0 rungs). By hand, the fork's headers against the live reads and the code read: `Bound`'s
hidden `AiGrid` at +0x18, `AiGrid.MetaData` +0x18 and `Subgrids` +0x68 (so its hidden `Pool` is 8
bytes), `AiSubgrid.SizeX/SizeY/TileGrid`, `AiGridTileData.Width/Tiles`, the 16-byte
`AiGridTile` with `MetaDataIndex` at +0xC, `InputController.PreviewTask` +0x58,
`CharacterTask.TaskInfo` +0x8 with `Succeeded` +0x15, the bound flags' names
(`ItemShootThroughBlock`, `ItemSeeThroughBlock`, `ItemWalkThroughBlock`) against what each object
blocks: **none found wrong.** One name that reads more than it holds: `TaskInfo.Error` stays 0
while the cursor says "path blocked" - the error mask `GetTaskInfo` turns into the warning is a
local of its own, not kept there.

**Wish for `GK.Memory`:** `GK.Memory.Tile(position)` - the address of the AI grid tile under a
point (the walk above, in the fork beside `GetTileDebugInfo`); and `Field` for a struct that is
no component's (`Bound` inside `BoundComponent` worked only because it is a component's field).
