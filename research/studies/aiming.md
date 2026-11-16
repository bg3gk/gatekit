# Aiming

What stands between an actor and a target, and what the cursor says about it. The tests are
under `tests/aiming/`; the verbs are `GK.Gui.Aim`, `Aiming`, `Confirm`, `CancelAim`
(`Client/Aim.lua`, [lua-api.md](../../docs/contracts/lua-api.md#aiming-clientaimlua-ui-side)).

## Rung 1: a clear aim, told truthfully

### Sources

- bg3.wiki, "Advantage": two d20 rolls, the higher counts; one source is as good as several.
  "Paralysed": attack rolls against the creature have advantage. Card `Projectile_FireBolt`:
  range 18 m, an attack roll. Card `Projectile_Fireball`: a 4 m radius. Read with
  `gatekit wiki rules Advantage`, `gatekit wiki card <id>`.
- The game's stats, read in the game: Fire Bolt's `TargetConditions` are
  `not Self() and not Dead()` (`GK.Gui.Aiming().shape`).
- Mods: `damage-preview` (unpacked with `gatekit mod unpack`, XAML only) shows the cursor's numbers
  by binding the player's view model: `CurrentPlayer.UIData.HitChanceDesc` (its advantages and
  disadvantages), `UIData.Cursor`, and `CurrentPlayer.CurrentTarget` - a view model of the hovered
  target that the hotbar's data context (`GK.Gui.Data("HotBar").CurrentPlayer`) does not carry.
  `bg3wasd` and `ai-allies` use nothing of the targeting (`gatekit mod usage`).
- Script Extender declares what was touched as read: `eoc::spell_cast::StateComponent` (`Caster`,
  `SpellId`, `Targets`, `CastPosition`, `SpellCastGuid`), `ecl::TextLine` (the context byte reads
  as `field_10`), `gui::VMHitChanceDesc` (`TotalHitChance`, `BaseHitChance`). None found wrong
  among them.

### What the game does, and how it was shown

| Claim | Evidence |
|---|---|
| The cursor's hit chance over a creature is the attack roll's; with advantage it is 1 - (1 - p)^2 of one roll's p, rounded (55 % one roll, 80 % shown over a paralysed goblin) | `tests/aiming/cursor/hit_chance` |
| A single-target spell aimed at the ground shows no chance | the same test |
| An aim the spell's target conditions exclude is marked invalid with the game's own reason ("cannot target self"), and the targeting stays open | `tests/aiming/cursor/refused_target` |
| Fireball outlines the three goblins around its point and not an ally 5.3 m away, and burns exactly the three | `tests/aiming/area/preview_is_the_blast` |
| While an action is aimed, the server already keeps a cast entity for it (`SpellCastState`, shared to the client) with no targets; the click that the server takes writes `Targets` and `CastPosition` into it in the frame the targeting closes, and adds `spell_cast::MovementComponent` | read live each tick around a click (a client tick watch, not kept) |
| Out of range and out of a fight the click is taken all the same: the cast's targets are written and the caster walks into range first; `SpellCastMovement.Position` stays zero meanwhile, so it does not tell the walk | live, Fire Bolt at a point 32 m away |
| The game picks from the real pointer every frame (`ecl::PickingHelper`): a value written into the pick is overwritten in the next frame. With the window in the background the pointer stays where it last was, so the idle cursor shows whatever lies there - in one copy the screen's corner (a walk to it), in another a staged goblin, which turns the idle targeting into the main-hand attack | live; W-0126 in the coverage record |
| A hostile goblin created by `GK.Stage.Spawn` and paralysed in the same chunk, never moved, stays hidden from the party: `Osi.IsInvisible` 1, an `InvisibilityComponent` holding its spot, `Osi.CanSee` 0, and an aim at it is "invalid target". Moved once where it stands (`TeleportToPosition`), it is seen. A neutral goblin and an unparalysed hostile one are seen at once | live, three goblins side by side; why is open (rung 2 and 5: what the party perceives) |

### The instability of `aim_and_observe` after `kits_and_attention`

The failures on record (copy `attacks`, the runs of 01:48 and 01:53) came before the clean
learned to carry strays. Two tests earlier (`fair_attack`, `refusals`) failed their clean with
the host in a fight and flanked; under `--attach` the world as it was then became the new
baseline, so the hostile goblin beside the host was part of the baseline and Verify called the
scene clean. `kits_and_attention` passed over it; `aim_and_observe`, which then pinned no
initiative, staged its goblins into a fight already running with a creature it did not stage,
and its clicks fell outside the host's turn (the pictures show the turn order and the creature
beside the host). Since the clean names and removes strays and the test pins the host's
initiative, `kits_and_attention` followed by `aim_and_observe` passes (run again, both speeds of
the new tests after it pass too).

What Verify still does not see, found on the way:

- **Strays one Lua state deep only.** `GK.Stage` reads the strays of the Lua state just before
  its own; after two bridge reloads a creature spawned two states back is taken into the next
  baseline, and Verify is clean with it standing there (seen: a probe goblin of this study was
  outlined by the next test's Fireball as a fourth target). A proposal for the stage's owner.
- **The client's side.** Verify reads the server only. The real pointer's hover, the targeting
  left open, and the aim still held are client state. `GK.Gui.Confirm` and `CancelAim` now give
  the aim back when the targeting closes; the pointer's hover stays outside our reach until the
  fork can set `ls::Mouse` (W-0126).

### Hypotheses

| Hypothesis | Source | Held? |
|---|---|---|
| A second aim at the same target loses the hit chance | the brief | not reproduced: three aims at the same goblin, at speed 1 and 8, after other aims and after a cancel, read 80 each time; the answer took the whole settle (about 0.7 s) instead. The test checks it, and the same aim made again now answers at once |
| The hit-chance line is the view model's line of the same index | the code as found | weak: the task's own line carries its context byte (0x80) and the view model follows some frames later; the task's byte now decides |
| The click's success shows as the targeting closing | the code as found | not enough: the targeting closes before the server's answer and also when the click is taken for something else; the cast's targets are the server's answer |

### Not yet

- An event for the view model's update: the settle after an aim at a new target that reads the
  same is a wait in real milliseconds. `CurrentPlayer.CurrentTarget` (damage-preview's binding)
  is the lead.
- The tracer and the memory instrument were not used in this rung: what was claimed about the
  cast entity was read from the components themselves.

## Rung 2: what blocks an aim

### Sources

- bg3.wiki, "Sight mechanics" (`gatekit wiki rules "Sight mechanics"`): ranged actions are of two
  kinds. A *target action* (Sacred Flame, Chill Touch, Misty Step) needs a line of sight; a
  *projectile action* (Fire Bolt, Fireball, every ranged weapon attack) needs no sight but an
  unobstructed physical path. Walls, closed doors, trees and large rocks are total concealment;
  surfaces carry `CanSeeThrough` and `CanShootThrough` (Fog: sight blocked, shots pass;
  Darkness: both blocked). "High ground rules" and "Obscurity" add bonuses and disadvantage, not
  blocking. The wiki states no cover mechanic.
- The game's data, read in the game: item root templates carry `CanShootThrough`, `SeeThrough`,
  `WalkThrough`, `IsBlocker`, `CanBeMoved` and a `PhysicsTemplate`; a wooden crate
  (`CONT_GEN_Crate_Wood_A`) is `CanShootThrough`, a barrel (`CONT_Barrel_A`) is not - the
  owner's observation that a crate does not block and a barrel does.
- Mods: of the study mods, only `easycheat` touches it - `CanShootThrough` and
  `CanShootThroughBoostComponent`, a boost that lets a character shoot through (`gatekit mod
  usage`). `ai-allies`, `combat-extender`, `community-library` use no sight or raycast call.
- Script Extender exposes what was needed, read only: `Ext.Level.GetTileDebugInfo` (the AI grid's
  tile with its flags by name: `ShootBlock`, `ShootBlockItem`, `WalkBlock`, `WalkBlockItem`,
  `StaticBlock`, `Door`, `DoorItemBlock` ...), `Ext.Level.RaycastAll/Closest` over the physics
  scene, `Ext.Enums.PhysicsGroupFlags` (`CanShootThrough`, `CanSeeThrough`, `CanSeeThrough2`,
  `Fadeable` ...), `ls::PhysicsComponent.PhysicsGroup`. None found wrong among them.

### What blocks, by kind, and how it was shown

| Obstacle | Target action (sight) | Projectile (path) | Walking | Evidence |
|---|---|---|---|---|
| The level's wall | refused "target out of sight", no hit chance | refused "path blocked", the hit chance still shown | blocked (tile `WalkBlock`) | `tests/aiming/blocking/level_walls_and_doors` |
| The level's closed door (its leaf read across the line, 5.3 degrees off square; `Osi.IsOpened` 0, `ItemISDoorClosed`) | refused, out of sight | refused, path blocked | | the same test |
| The same door, opened (`IsOpened` 1, no tag), then closed again | valid; refused again | valid; refused again | | the same test |
| An object created at run time, out of a fight | valid (a walk round it when it blocks) | valid (the same) | | `tests/aiming/blocking/placed_objects` |
| The same in a fight with no movement left, half way down 8.75 m: a barrel (1.3 m), a wine barrel (2 m) | refused "not enough movement" behind the wine barrel; valid past the barrel | refused "not enough movement" behind both | blocked (`WalkBlockItem`) | the same test (printed, not asserted: not explained) |
| A door created at run time, turned across the line (`Osi.ToTransform`, 0 degrees off square), in the fight with no movement: closed, opened, closed again | refused "not enough movement"; valid; refused | the same | tile `Door, DoorItemBlock, ShootBlockItem` while closed, nothing while open | the same test (asserted) |
| A wooden crate created at run time (`CanShootThrough`), its box across the line, 0-1 m high | valid | valid | climbed (`CanClimbOn`) | the same test |
| A bottle rack created at run time (`CanShootThrough`) | valid | valid | `WalkBlockItem` | the same test; **proves nothing**: its box hangs from 1.43 to 3.09 m and the line passes under it |
| Any of these, the caster 3.5 m to the side | | valid again | | the same test |

- **The cursor judges the walk, not the line.** For a projectile and for a target action alike,
  the task (`ecl::CharacterTask_Spell`) checks the line from where the caster stands; where it is
  blocked, it looks for a place to act from (`MoveInSpellRangeCache`, its `Solution` holding the
  place) and its verdict is on the walk there (`PathDistance`): taken when the caster can walk it
  (out of a fight, any length), refused "not enough movement" when not, refused with the line's
  own reason ("path blocked", "target out of sight") only when no place is found. The click then
  walks the caster to that place before the cast. See rung 3, "The straight path against the
  cursor", for how this was found.
- **A door blocks by its state, the level's and a placed one alike.** Closed across the line it
  refuses both kinds, opened neither, closed again both (the level's door out of a fight with the
  line's own reasons; the placed one in a fight with no movement, for the walk). WITHDRAWN
  (2026-11-15, the owner's check of the geometry): "the created closed door blocks sight and not
  the projectile" and "the level's door differs from a placed one" - the placed door was made
  unrotated (`GK.Stage.Spawn` takes no rotation) and its leaf lay along the line, the line inside
  its 0.3 m thickness. Every placed object's box is now read against the line in the test.
- **What a placed object blocks is its bound's flags** (rung 6, the descent). In a fight with no
  movement, the barrel blocks the projectile and not sight, the wine barrel both, the crate
  (`CanShootThrough`) neither. Read on each: `Bound.Flags` `ItemShootThroughBlock` (absent where the
  template's physics groups hold `CanShootThrough`) goes with blocking the projectile and is
  marked on the tile as `ShootBlockItem`; `ItemSeeThroughBlock` with a `ServerIsVisionBlocker`
  (absent with `CanSeeThrough2`) goes with blocking sight, and the tile has no flag for it. The
  barrel has the first only, the wine barrel and the closed door both, the crate neither. Tried on
  five objects; not yet asserted in a test. The tile is written by
  `eoc::AiGrid::AddObjectToTile` / `RemoveObjectFromTile` / `UpdateTileObjectFlags`
  (`bg3_dx11.exe+0xE38AE0`, `+0xE38D70`, `+0xE33310`), driven by `eoc::BoundComponent::Increase` and
  `Decrease` from `esv::Item::UpdateBoundFlags`.
- **A door's tile follows its state at the next tick**: `Osi.Open` clears `Door, DoorItemBlock,
  ShootBlockItem` from its tiles one tick later and `ItemISDoorClosed` goes; `Osi.IsOpened` turns
  1 some 33 ticks later (the animation). `Osi.Close` the reverse: the flags and `IsOpened` 0 at
  the next tick, the tag 33 ticks later. A door marks no `WalkBlockItem`. (A probe recording every
  tick; TestLevel_Tom, the placed door.)
- **`Osi.CanSee` is a third answer.** It read 0 behind a created wine barrel, a created door and a
  movable barrel while the target action was valid out of a fight, and 1 through the invisible
  blocker of the level (`PUZ_InvisibleBlocker_*`, physics groups `CanShootThrough` and
  `CanSeeThrough2`), which a raycast hits and which refuses neither aim. It is not what the cursor
  uses; a test that needs the cursor's sight asks the cursor.
- **A placed piece joins navigation.** Its tile is marked a moment after it is created (no event
  of it is known; the test waits for the mark), and cleared when it is removed.
- The cursor's hit chance stays on a projectile whose path is blocked (the roll is known, the path
  is not free) and is absent for a target out of sight.

### The goblin hidden from the party

A creature given an incapacitating status (`PARALYZED`, `SLEEPING`) in the same Lua chunk that
creates it stays hidden from the party: `Osi.IsInvisible` 1, an `InvisibilityComponent` holding
its spot, `Osi.CanSee` 0, the aim "invalid target" - for a neutral goblin as for a hostile one,
in the sandbox and in TestLevel_Tom. A status that does not incapacitate (`BLESS`) leaves it seen;
the same paralysis given in a later chunk leaves it seen; moving the hidden one once in place
(`TeleportToPosition`) reveals it. The cause is the order of the test's own raw calls, not
`GK.Stage.Spawn`. Which system holds a creature unrevealed until its first update is open.

### Hypotheses

| Hypothesis | Source | Held? |
|---|---|---|
| A crate does not block and a barrel does | the owner | held for the projectile: a `CanShootThrough` template blocks neither kind; of the others, which kind each blocks is not explained (above) |
| The projectile is refused exactly when a tile on its line is `ShootBlockItem` | rung 2's first readings | not held: those readings caught the task's first verdict, before the walk (rung 3); the movable barrel marks it and leaves sight free (the door's "lets the projectile pass" withdrawn: it stood along the line) |
| A target action's sight ignores every object created at run time | rung 2's first readings | not held: out of a fight the walk mends it; with no movement the wine barrel and a closed door across the line block sight |
| A placed door does not block because it comes into the world open | the owner | not held: it is made closed (`Osi.IsOpened` 0, `ItemISDoorClosed`); unrotated it stood along the line, which is why it seemed to let the projectile through |
| Only items that cannot be moved mark the grid | the first readings | not held: a movable barrel marks `ShootBlockItem` |

### Not yet

- High ground (range and the roll's bonus) and obscurity: rung 3 and later; nothing measured.
- The path a projectile would take: `ecl::spell_cast::TargetingComponent.Path` stays empty while a
  spell is aimed; the trajectory drawn is not read.
- What a placed object blocks, for which kind: the descent (rung 6), with the tracer over an aim
  refused and the same aim taken, and over the task's straight check.

## Rung 3: three kinds of reach

### Sources

- bg3.wiki, "Extra Reach" and "Main Hand Attack": a melee weapon reaches 1.5 m, a reach weapon
  (glaive, halberd, pike) 2.5 m; what the distance is measured between is not said. "Weapons": a
  ranged weapon has a normal and a long range, the long one with disadvantage and "not more than
  5%" longer, not measured. "High ground rules", "RangeIgnoreVerticalThreshold (spell flag)",
  "HasHighGroundRangeExtension (spell flag)": range is a vertical cylinder around the attacker;
  most ranged actions ignore height and reach farther downwards; the long-range disadvantage is
  measured by the true distance; spell attacks have no such penalty. "Throw": range from the
  object's weight and the thrower's Strength, 3 to 18 m.
- The wider search (one web search on how melee range is measured): nothing beyond the wiki.
- The game's stats, read in the game: `Target_MainHandAttack` has `TargetRadius`
  `MeleeMainWeaponRange` and no `RangeIgnoreVerticalThreshold`; `Projectile_MainHandAttack`
  `RangedMainWeaponRange` with it; `Throw_Throw` is `SpellType` Throw, `ThrownObjectRange`, with
  `IgnoreVisionBlock`. Weapons carry `WeaponRange` in centimetres (dagger 150, glaive 250,
  shortbow 1800).
- Script Extender declares what was read: `eoc::BoundComponent` (`Bound.AIBounds` with the
  `Stand`, `Move` and `Hit` cylinders, each a radius and a height), `ecl::CharacterTask_Spell`
  (`MoveInSpellRangeCache`, `CanReachTarget`, `PathDistance`, `PathLimitDistance`, `Result`).
  None found wrong among them.

### What the game does, and how it was shown

| Claim | Evidence |
|---|---|
| A melee attack reaches the weapon's `WeaponRange` plus the `Hit` radius of the attacker and of the target, between their centres on the ground: dagger 1.5 + 0.2 + 0.2 = 1.9 m to a goblin, + 0.7 = 2.4 m to an ogre; glaive 2.9 and 3.4 m. Not the `Stand` (0.5, 1.2) or `Move` (0.25, 0.5) radii | `tests/aiming/reach/melee_reach`; sweeps by 0.02 m: the goblin taken at 1.90, refused at 1.92 |
| A ranged weapon's normal range is its `WeaponRange` centre to centre; beyond it, up to the range plus the two hit radii, the attack is taken with one disadvantage ("target outside normal range"): the wiki's unmeasured long range is exactly the radii | `tests/aiming/reach/ranged_range`; the shortbow: no penalty at 17.98, penalty at 18.02, taken at 18.38, refused at 18.42 |
| A ranged spell (Fire Bolt) reaches as far as the weapon, with no penalty in that band | the same test |
| With movement left, a target out of reach is taken and walked to; with none, the cursor refuses "not enough movement": reach shows as the walk the game would need | the same tests, and rung 1's walk out of a fight |
| The edge is not sharp to a few centimetres in the cursor: a walk of 0.06 m with no movement left was taken once and refused another time | the task read during a failing run (path distance 0.063, limit 0) |
| Throw is aimed in two clicks: the object (an item on the ground), then where it goes. While the object is hovered the task already holds it as its `Result.Target`; the click that picks it adds `NeedsPreviewToActivate` to the task's `Flags`, drops the entity from the task's `Target`, and leaves the targeting open. `Targets` stays empty; the cast's targets hold only where it goes | `tests/aiming/reach/throw_over` (`Confirm` answers `picked`); the task read before and after the click |
| A throw is judged by its arc. In a fight with no movement, a goblin 6 m away: a 1.3 m barrel anywhere from 1.5 to 5.2 m lets the throw pass (Fire Bolt is refused from 2.5 m on); a 2 m wine barrel lets it pass at 2.5 and 3.5 m and refuses it at 1.5, 4.5 and 5.2 m, near the ends of the line where the arc is low | `tests/aiming/reach/throw_over` (four places); a probe over ten places |
| A throw past an obstacle is refused "path blocked", not "not enough movement": with its object picked it looks for no other place | the same test |
| The flight agrees with the cursor: the dagger thrown over the wine barrel half way lands 0.3 m from the goblin and hits it; the host does not move | the same test |

### The straight path against the cursor

The same aims past a placed barrel read refused at one time and taken at another. What decides
is the walk, and when the verdict is read:

| Claim | Evidence |
|---|---|
| The task's verdict on an aim past a barrel changes within the first picking updates: for about 20 ms (three or four updates of the `PickingHelper` system) it reads "path blocked" with the hit chance and no path distance (`PathDistance` at -FLT_MAX); then the place to act from is found (`PathDistance` 1.0 in the corridor) and the line becomes the walk's - valid out of a fight, "not enough movement" in a fight with none | a probe recording the task's text lines and move fields on every client tick from the aim, TestLevel_Tom, a goblin never aimed at before, at speed 1 and 8 |
| Held for a minute, the verdict does not change again | the same probe, reads every 5 s |
| The draw rate of the window does not enter: at 4, 10 and 30 frames a second the course and the final verdict are the same, and the picking updates are not tied to the frames drawn (more updates per second at 4 than at 30) | `gatekit window fps aim <n>`, the same probes |
| Out of a fight, the click on an aim past a wine barrel walks the host 7.2 m (to `MoveInSpellRangeCache.Solution`, 6 m on and 4 m aside) before the bolt; past a barrel in the corridor, 1 m | a probe in the sandbox; `placed_objects` |
| In a fight with no movement, a barrel 1, 2 or 3 m from the caster on an 8 m line lets the bolt pass with no walk; at 4 to 7 m it needs one (refused "not enough movement") | a probe in the sandbox, the barrel moved by 0.5 to 1 m |

So the refusals that rung 2's `placed_objects` asserted were the task's first verdict: `Aim` then
answered at the view model's first change after the aim, before the walk was judged; since `Aim`
waits its settle (rung 3), the same aims read taken. The draw rate of a deck tile is not the
cause. A test that wants an obstacle's refusal makes the aim in a fight with the movement spent.

What the game waits for during those updates is the search for the place to act from. `Aim`
still waits its settle (600 ms and ten picking updates), which covers it many times over; no
field was found that says "the search is done" in every case (`MoveInSpellRangeCache.field_0`
became 1 in some cases and stayed 0 in others; `PathDistance` keeps a stale value from an earlier
aim when no walk is needed).

### Hypotheses

| Hypothesis | Source | Held? |
|---|---|---|
| Melee range is measured from the edges of the creatures | the engine's bounds | held, with the `Hit` cylinder as the edge |
| The long range of a ranged weapon is a fixed share of its range | the wiki's "not more than 5%" | not held: it is the two hit radii (0.4 m for two medium creatures, 2 % of 18 m) |
| Height enters the projectile's path (a low object near the caster lets it pass) | the first sandbox readings | held as an observation in a fight with no movement (a barrel in the first 3 m of 8 lets the bolt pass, from 4 m on it does not); the trajectory itself not read |
| A thrown object goes over an obstacle that stops a straight shot | the wiki's "Throw", `IgnoreVisionBlock` in the stats | held, as an arc: over a low obstacle anywhere, over a tall one only near the middle of the line |
| The verdict depends on the window's draw rate (a deck tile drew 10 frames a second) | the coordinator | not held: 4, 10 and 30 frames a second give the same course; the cause is the walk and the moment of the read |

### Not yet

- Melee's vertical limit (it lacks `RangeIgnoreVerticalThreshold`) and the downward extension of
  ranged reach: the sandbox is flat; a level with height is needed.
- The flight of a projectile against the cursor's verdict: the trajectory is not read; for a
  throw, the one flight over the wine barrel agrees.
- The arc's height as a number (by distance, by the object's weight), and a lobbed spell besides
  Throw: not measured.
- An event for the cursor's verdict: `Aim` waits the settle.
- Whether Jump takes two clicks; Throw's range by the object's weight.

## Rung 4: the area's preview against the blast

### Sources

- bg3.wiki, "Fireball": a 4 m radius around the point; a flame that "explodes upon contact". "D&D
  5e spell changes": areas are given in metres, spheres usually 4 m. Neither says what "within"
  means for a creature's size. "Invisible": an invisible creature cannot be seen. A search of the
  wiki for previews that lie found nothing beyond hit-chance notes (`gatekit wiki find`).
- This study, rung 3: a creature's edge for reach is its `Hit` radius (the first hypothesis).
- The game, read in the game: `Projectile_Fireball` has `ExplodeRadius` 4; the preview's outline
  is `ecl::spell_preview::HighlightsComponent` on the cast's preview entity (`GK.Gui.Aiming().affected`).
- Mods: rung 1's survey only - `damage-preview` binds the cursor's hit chance and damage, not the
  area's outline; no further search for this rung.

### What the game does, and how it was shown

| Claim | Evidence |
|---|---|
| A round area touches a creature whose centre, on the ground, is within the radius plus the creature's `Hit` radius: a goblin (0.2) at 4.15 m from Fireball's point and not at 4.25; an ogre (0.7) at 4.65 and not at 4.75. The preview outlines exactly those | `tests/aiming/area/edge_of_the_blast`; a probe ring from 3.5 to 5.2 m before |
| The preview outlines only creatures the party sees; the blast touches the unseen inside it too. An invisible goblin and an invisible ally (`INVISIBILITY`) 2.5-3 m from the point burn and are not outlined | `tests/aiming/area/preview_shows_the_seen` |
| A creature paralysed in the chunk that made it is unseen the same way (`IsInvisible` 1) and is likewise burned and not outlined. This is the ally that rung 1's preview "left out" at 3.9 m | a probe ring (goblin and ally at 3 m) |
| An invisible creature on the projectile's flight catches it: the blast goes off there - it burns the invisible goblin 7 m out and one 3 m beside it - and the goblin outlined 3.5 m past the point is spared | `tests/aiming/area/unseen_on_the_path` |
| The same goblin seen on the line does not stop the bolt: the blast burns the outlined goblin past the point and nothing near the line | a probe of the same layout without the invisibility |

So the preview and the blast differ exactly where the party cannot see: an unseen creature inside
the area is burned and not shown, and an unseen creature on the flight moves the whole blast. For
a runner that chooses where to aim by the preview (`GK.Cheat.AimBest`, rung 5), an area is safe
for an ally only as far as the party sees.

### Hypotheses

| Hypothesis | Source | Held? |
|---|---|---|
| The area's edge for a creature is its `Hit` radius beyond the area's radius | rung 3 (reach) | held, for goblin and ogre, to 0.05 m |
| The preview leaves out a creature at the edge that the blast reaches (rung 1's ally at 3.9 m) | rung 1's observation | not held as an edge: preview and blast agree at every distance for seen creatures; the ally was unseen |
| A creature on the flight line stops Fireball | the wiki's "explodes upon contact" | held for an unseen creature only |

### Not yet

- Other shapes: a cone, a line, a cylinder with height, and whether height or cover between the
  point and a creature takes it out of a round area; one shape, one rule each.
- Why a seen creature on the line does not stop the bolt (the trajectory planned over it, or the
  projectile passing through seen creatures) - the trajectory is not read.
- `Osi.CanSee` read 0 for a goblin behind an invisible one on a diagonal while seen goblins in
  front of another did not hide it: one reading, not followed.

## Rung 5: whom the party perceives

### Sources

- Script Extender declares the server's sight state: `sight::DataComponent` (`SightData`: range,
  field of view, darkvision, a `SightUuid`), `sight::EntityViewshedComponent`
  (`SightEntityViewshed.Viewshed`, a set of sight uuids), `esv::sight::ViewshedParticipantComponent`
  (`ServerViewshedParticipant.CanSee`, a set of entities), `esv::sight::AggregatedDataComponent`,
  the one-frame events `ServerSightEvents` and `ServerSightEntityViewshedContentsChanged`;
  `InvisibilityComponent` (`Invisibility`, three unnamed fields) on server and client; `Stealth`.
- The symbols: `eoc::invisibility::IsInvisible`, `Legacy_IsInvisible`, `CanBeHiddenFromClient`,
  `ecl::EntityConversionHelpers::IsEntityInvisible`.

### What the game does, and how it was shown

| Claim | Evidence |
|---|---|
| "Invisible" is the presence of the `Invisibility` component on the creature (after resolving a hit proxy): one global state, not one per observer | `eoc::invisibility::Legacy_IsInvisible` (EoCShared.dll+0x10A2860, bg3.exe+0xB0B1E0) tests for the component and nothing else; the client's `ecl::EntityConversionHelpers::IsEntityInvisible` (EoCClient.dll+0x98BDB0) calls it |
| Whether an invisible creature is hidden from a client depends on who it is to that client: one of the same party is never hidden; others can be | `eoc::invisibility::CanBeHiddenFromClient` (EoCShared.dll+0x10A2500) compares the party and user of the two and then their relation; read, not traced |
| What the sight system keeps per observer is kept on the one seen: `ServerViewshedParticipant.CanSee` on X is the set of characters whose sight holds X - within the observer's sight range (18.5 m for the host), a clear line, X not invisible, in every direction (no field of view) | the sandbox: goblins at 6 m north, south, east and west all held by the host; 19.5 and 25 m not; an invisible one held by nobody. Equal to `Osi.CanSee` pair by pair, a hireling apart (below) |
| The area's preview outlines every creature the party is shown, beyond every member's sight: two goblins 18.9 and 19.5 m from the host, out of its sight (`CanSee` set, `Osi.CanSee` 0), were outlined by Fireball at 16 m; the same one made invisible is not | the sandbox, one aim; rung 4's `preview_shows_the_seen` |
| A creature behind TestLevel_Tom's wall or closed door is shown (no `Invisibility`) and in no member's sight; the client fades it with the room's triggers (`ClientRoomTriggerCharacterFade*`), the camera's doing and not perception | TestLevel_Tom: the goblins of `level_walls_and_doors` at their places, one in the corridor in sight |
| A hireling waiting in `SYS_Hirelings` is on stage, has no `Level` component, no entity on the client, and `Osi.CanSee` 1 from the host 3.5 m "away" | the sandbox: nine such characters around the start point |

So "the party perceives X" is, as the player is shown it: X has no `Invisibility` component, or X
is of the party. Sight (who holds it now) is a second, narrower answer that the cursor uses for a
target action ("out of sight") and the preview does not. `GK.See.Perception` reads both.

### Hypotheses

| Hypothesis | Source | Held? |
|---|---|---|
| The party perceives what is in some member's sight | the coordinator's framing, `Osi.CanSee` | not held for what is shown: the preview outlines creatures beyond every member's sight range; held for a target action's sight |
| A sneaking NPC is hidden from the party out of its sight | the wiki "Stealth" | not staged: `SNEAKING` applied to a goblin 25 m away gave no `Invisibility` and left it shown; one 12 m away lost the status at once |
| `SEE_INVISIBILITY` on the host puts an invisible goblin into its sight | the status's name | not held as staged: the status has no boosts, the set and `Osi.CanSee` stayed empty |

### Not yet

- Where the sight system writes `ServerViewshedParticipant.CanSee`, and what adds and removes
  `Invisibility` (the descent, rung 6).
- `GK.See.Around` admits the hirelings (its level check passes a character with no level) - for
  the owner of `Situation.lua`.
- A sneaking NPC hidden by the game's own detection; See Invisibility through the spell itself.

### The rule a fair `GK.Gui.AimBest` will be held to (the owner, 2026-11-15; not built)

The fair search leaves the creatures the party does not perceive out of everything it weighs:
not only out of whom an area would touch, but out of what stands on the line of the aim. It
neither looks for nor steers round an unseen creature on the flight line. An aim it chose may be
stopped on the way by an enemy it could not count - the bolt or the whole blast landing there -
and that is the right outcome, for a single target and for an area alike. The game does the same
(rung 6): a seen creature on the line makes the caster step to a clear line before the cast; an
unseen one is not counted and the flight stops at it - for Fire Bolt and for Fireball.

## Rung 6: the descent (the machine code)

The chronicle (`research/proof-of-concept/reports/aiming.md`, "Run of the night's second half")
has every call and its answer. What is understood:

| Question | Where it is decided | Evidence | Names given |
|---|---|---|---|
| What makes a placed object block a walk, a projectile, sight | its `Bound.Flags` (`ItemWalkThroughBlock`, `ItemShootThroughBlock`, `ItemSeeThroughBlock`, absent where the template's physics groups hold `CanShootThrough` / `CanSeeThrough2`) and, for sight, its vision blocker; the AI grid's tile carries the walk and shoot flags (`WalkBlockItem`, `ShootBlockItem`), none for sight | the flags read on five objects against the cursor's verdicts (`placed_objects`) | - |
| Who writes the tile a moment after the object is made, or a door opens | `eoc::AiGrid::AddObjectToTile` / `RemoveObjectFromTile`, which rebuild the tile with `UpdateTileObjectFlags`, driven by `eoc::BoundComponent::Increase/Decrease` from `esv::Item::UpdateBoundFlags`; a door's tile changes at the next tick after `Osi.Open/Close` | a write watch on the tile reached through `Bound.AiGrid` | `bg3_dx11.exe+0xE38AE0`, `+0xE38D70`, `+0xE33310`; the grid's fields (`decomp field`) |
| Where the cursor's verdict is kept and written | `ecl::CharacterTask_Spell.TaskInfo` (`Succeeded`, the warning in `Texts`), rewritten every picking update by `GetTaskInfo` from `SetCursor` < `UpdatePreview` < `ecl::InputController::UpdatePreview`; the warning is the text of an error mask from `CharacterTask_Spell::GetError` over `CharacterMoveTask::GetError` | a client write watch on `Succeeded`; the tracer sees no component of it | `bg3_dx11.exe+0x1B40730` |
| Which bit is "path blocked" / "not enough movement" | not yet: the mask is a return value; the walk's own bytes (`CanReachTarget`, `IsPathLimited`) read the same for a refused and a taken aim behind a wall | - | - |
| Why an unseen creature on the line stops a bolt and a seen one does not | the seen one is counted as an obstacle: the caster steps 1 m aside to a clear line before casting (Fire Bolt and Fireball alike); the unseen one is not counted, the caster casts from where it stands and the flight stops at it. The cast's request (its target point) is the same in both; only `Invisibility` and `Stealth` on the goblin differ | probes: the server's tracer over each Fireball cast; the host's place before and after, Fire Bolt and Fireball, seen and invisible | - |
