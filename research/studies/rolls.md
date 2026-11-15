# Study: the dice

How a roll of Baldur's Gate 3 is asked for, drawn, shown and resolved, and how a test decides it in
advance. The tests are under [tests/rolls](../../tests/rolls); the verbs are `GK.Rolls`
([lua-api.md](../../docs/contracts/lua-api.md), section 17).

## 1. The pin

### The rule, from bg3.wiki and wider

- bg3.wiki, "Dice rolls" (natural 1s and 20s): on an attack roll and an ability check a natural 20
  always succeeds and a natural 1 always fails; most saving throws do not follow that rule, but those
  made to keep concentration and those of a dialogue do.
- bg3.wiki, "Dice rolls" (Karmic Dice, Karmic Debt): the option, on by default, weights attack rolls,
  targets' saving throws and dialogue checks, not checks outside a dialogue (lockpicking,
  Perception). A failed roll adds its chance of success to a debt; while the debt is positive a
  hidden roll forces a success, picking a die that succeeds. The page's formula for the chance is
  marked as garbled. Reliable Talent - a `MinimumRollResult` boost in the game's data - is said to be
  overridden by the forced failures of Karmic Dice; the page cites a Larian forum hotfix post, a
  PC Gamer and a PCGamesN article, and a Reddit write-up.
- `gatekit wiki rules` and `wiki page` give 6000 characters unless `--chars <n>` asks for more;
  the rest of "Dice rolls" was read on the web page itself.

### What the mods do

- The game's own data pins dice the same way: Reliable Talent is
  `MinimumRollResult(SkillCheck,10)` and the like under a condition (`gatekit data stat
  ReliableTalent`).
- EasyCheat's "always succeed" boosts are `MinimumRollResult(SkillCheck,20)` with a large
  `RollBonus`, a minimum alone; Ancient Mega Pack and Item and Spell Bug Fixes use
  `MinimumRollResult(Damage,n)` and `(SavingThrow,n)` in items and passives. No study mod pins with
  the pair `MinimumRollResult;MaximumRollResult`, switches `WeightedRolls`, or listens to the
  finished-roll event. Wish: a mod made for Karmic Dice (an "always karmic" or "no karmic" mod) to
  read how it reaches the debt.

### What Script Extender declares

- `esv::roll::RollFinishedEventOneFrameComponent` (Lua `ServerRollFinishedEvent`, one frame) with
  `Array<FinishedEvent> Events` (`gatekit symbols layout`; the fork's `Components/Roll.h`). The fields
  the bridge reads - `Roller`, `Subject`, `RollType`, `Skill`, `Ability`, `DC`, `NaturalRoll`,
  `DiceAdditionalValue`, `RollContext`, `Canceled`, `Roll` - gave values that agree with the game's
  own window and with the pin: the natural die equal to the pin, the difficulty equal to the window's,
  the bonus equal to the window's listed modifiers, the kind `SkillCheck` / `SavingThrow` as asked.
  `Advantage` and `Disadvantage` are not checked yet (rung 3). None found wrong among what was
  touched.

### Hypotheses and whether each held

| Hypothesis | Source | Held? |
|---|---|---|
| The pair `MinimumRollResult(K,n);MaximumRollResult(K,n)` fixes the natural die of the holder's rolls of kind K, passive or active, and leaves modifiers, difficulty and verdict to the game | the game's own boosts | held: `pin/passive`, `pin/active_window` |
| A save the story asks for follows the natural-20 rule (20 passes difficulty 99) | the wiki's dialogue exception | held: `pin/passive` |
| Karmic Dice does not override a pin | the earlier proof, made with passive Perception checks - rolls the wiki says Karmic Dice does not weight | **did not hold**: a dialogue check pinned at 1 with Karmic Dice on came out a forced success above the pin in every run of `pin/karmic`, the boost still on the host |
| The forced success comes after one failure of a check with a fair chance | the wiki's formula | did not hold as stated: two to four failures came first; the wiki marks the formula as garbled |
| Taking a pin off and adding the same boost at once leaves one boost | - | **did not hold**: both were gone a few ticks later; `Pin` of the same n now keeps the pin |
| The roll window's opening row carries the modifiers once its roller is set | the bridge's own reading | not reproduced as a fault in any opening watched at speeds 1 and 8 (a reading every frame: roller, difficulty and modifiers arrived in one frame). The row now waits for the window's own sign that it waits for the throw (`WaitForStart`) |

### Open

- Whether a saving throw of a spell, pinned at 20 against a difficulty above 20 and its bonus, fails,
  as the wiki says of most saves.
- Where Karmic Dice keeps its debt and how the forced die is picked: 17, 17, 13 and 18 against a
  target of 13 in the runs of `pin/karmic`, after two to four failures.
  The descent of rung 6 is where it belongs.
- Whether Karmic Dice overrides a pinned attack roll and a pinned save, as the wiki's list suggests.

## 2. Rolls in a dialogue

### The rule, from bg3.wiki and wider

- bg3.wiki, "Dice rolls" (during dialogue): before a dialogue check an "Add Bonus" button lets the
  player character and other party members add the bonuses of certain spells and items; some story
  checks allow none. Guidance adds 1d4 to the recipient's ability checks.
- bg3.wiki, "Inspiration": after a failed roll made through the d20 roll menu, "Roll Again" spends
  one inspiration point; the points are the party's, at most 4; the modifiers of the first throw,
  Guidance's die among them, carry over.
- bg3.wiki, "Guidance": a cantrip; its spell is `Target_Guidance`, its status `GUIDANCE`
  (`gatekit data stat Target_Guidance`: `ApplyStatus(GUIDANCE, 100, 10)`, concentration).
- The dialogue used: the game's own `LOW_NGB_CountingHouse_UseForbiddenItem` (`gatekit data dialog
  ... --tree`): its first answer is a Persuasion check, DC 15; the success node jumps to the guard
  letting the speaker off with a warning, the failure node answers that looking is crime enough and
  sets the flag `TemporaryHostilityAfterDialog` on the guard. The flag is named by the shared story
  goal `_Greevers_Little_Helpers` (`gatekit data refs`), not read here.

### What the mods do

- No study mod's Lua or XAML names the window's `AddModifierCommand`, `RollAgainCommand` or
  `SelectedBoostModifierList` (a search over their files). Wish: a mod that skips or
  automates dialogue rolls (an "auto-roll" or "no dice animation" mod), to read which view-model
  commands it uses.

### What Script Extender declares

- `FinishedEvent.ConsumedInspirationPoint` (`Components/Roll.h`): true on the one finished roll of a
  window thrown again for a point - it agrees with the window's count of the point (2 to 1 to 0).
- The roll window's view model is not declared by Script Extender (it is the game's Noesis model);
  read through `GetAllProperties`: `SelectedBoostModifierList`, `NearbyPartyCharacters[i]
  .BoostModifierList` of `ls.VMBoostModifier` (`BoostModifier`, `DiceTypeSet`, `Min/MaxBonusValue`,
  `IsSelected`, `Owner`), `InspirationPoints`, `RollState` with `WaitForReRoll`.
- The inspiration points are the action resource `InspirationPoint`
  (`a9c98304-08e7-44b5-aaf9-da2ef5a50672`) of one entity without a uuid, not of a character.
  Written directly and replicated, the window follows. The cap is the definition's `MaxValue` (4,
  `PartyActionResource`); the row's own `MaxAmount` starts at 0 and grows as points are given.

### Hypotheses and whether each held

| Hypothesis | Source | Held? |
|---|---|---|
| The dialogue follows the window's verdict, and a pin decides it both ways | the dialogue's two RollResult nodes | held: `dialogue/branch` |
| A failure sets `TemporaryHostilityAfterDialog` on the guard | the dialogue's data | held, seen with a listener on Osiris `FlagSet`: set on the guard, cleared again the next tick; no hostility followed in the sandbox. Not in a test: the event log has no flag rows |
| A throw again is decided by the same pin | the pin binds every roll of its kind | held: `dialogue/reroll` |
| The server finishes a roll for each throw | - | **did not hold**: one finished roll a window, the last throw, `ConsumedInspirationPoint` set |
| The d20's pin decides the bonus die too (as a damage pin decides every die) | the contract of the damage kind | **did not hold**: pinned at 1, Guidance's d4 came 3 and 4 (`dialogue/bonus`) |
| Guidance offered in the window is a cast that leaves the status | the spell's data | held: `GUIDANCE` on the roller afterwards; the next check lists it as a modifier of value 0 and adds its die by itself, and offers no Guidance again |
| `Osi.GiveInspirationPoints` with a negative amount takes points | - | did not hold: nothing changed |
| Guidance added in the window counts at any speed | the speed ramp's verdicts up to 12 | **did not hold**: it is cast at the throw, and above speed 1 the roll can take its bonus before the status lands - at 8 always. `dialogue/bonus` runs at speed 1 |

### Open

- What `TemporaryHostilityAfterDialog` does in a real level (the goal that clears it).
- A story check that allows no bonus; the Dark Urge's checks are the wiki's example.
- ~~A check with advantage in a dialogue (rung 3)~~: section 3, `dialogue/advantage`.

## 3. Advantage and disadvantage

### The rule, from bg3.wiki and wider

- bg3.wiki, "Advantage": with advantage two d20 are rolled separately and the higher counts, with
  disadvantage the lower; several sources give no more than the second die; advantage and
  disadvantage together cancel, and one die is rolled as if there were neither. Only d20 rolls
  (attacks, saving throws, ability checks), never damage.
- The game's data gives them as boosts: `Advantage(Skill,Stealth)` (the passive
  `DeepGnome_StoneCamouflage`), `Disadvantage(Skill,Stealth)` (scale mail without Medium Armour
  Master), `Advantage(Skill, Intimidation)` (the status of Thaumaturgy) - `gatekit data grep`.

### What the mods do

- Not searched for this rung beyond the game's own data: the boosts are the game's, and the tests
  use them as its data does.

### What Script Extender declares, and what the game fills

- `eoc::RequestedRollComponent` (`RequestedRoll`): `Roll.Advantage`, `Roll.Disadvantage` (the
  flags of the request), `AdvantageType`, `Result.NaturalRoll`, `Result.DiscardedDiceTotal`.
  Read live: on a passive roll with an Advantage boost `Roll.Advantage` is true, while
  `AdvantageType` stays `None` and the finished event's own `Advantage` / `Disadvantage` stay false.
  On an active roll (the window) the finished event's flags are set - and the request is already
  destroyed when that event comes (its entity is `[DEAD]`); it is read in its destruction
  (`Ext.Entity.OnDestroy("RequestedRoll")`), where it still holds its result.
- `Result.DiscardedDiceTotal` is set on every roll, a plain one too: the game draws a second d20
  each time. With neither and with both, the kept die is the higher of the two as often as the
  lower.
- The roll window: `Roll.RollAdvantageType` ("Advantage", "Disadvantage"), `RolledNumber1` and
  `RolledNumber2` (both dice), `FinalResult` (the one kept) - as `Client/Popups.lua` read them
  already; now checked against the server.

### Hypotheses and whether each held

| Hypothesis | Source | Held? |
|---|---|---|
| With advantage the higher of two d20 counts, with disadvantage the lower | bg3.wiki, "Advantage" | held, per roll: `advantage/two_dice` (16 of 16 each), `dialogue/advantage` (the window and the server) |
| Advantage and disadvantage together give one die as if neither | bg3.wiki | held as far as it can be seen: the kept die is the higher and the lower alike, as with neither (`advantage/two_dice`); a second die is drawn all the same |
| The finished-roll event says when advantage applied | its fields `Advantage`, `Disadvantage` | **did not hold** for passive rolls (false with the boost on); held for active rolls. The rows read the request |
| A pin decides the kept die only | - | **did not hold**: it holds both dice at n (`advantage/pin`): advantage does not lift a die pinned at 1, disadvantage does not lower one pinned at 20 |

### Open

- An attack roll with advantage: no data the bridge reads carries an attack's natural dice
  (`game_hit` has the outcome only).
- From a fresh start of the copy the first passive Perception checks of the host repeat exactly
  (9/11 11/9 5/17 9/1 ... in two cold starts, the same pairs): section 4 says why.

## 4. A save, then a load

### The rule, from bg3.wiki and wider

- bg3.wiki has no page on how the dice are seeded. "Divine Intervention" (notes): the items of
  Bestowed Bounty are tied to the save - the first cast on a save always gives the same items, so
  reloading for a better chest does not work. That is loot, not a d20.
- Players: reloading to throw a failed dialogue check again is the common meaning of save-scumming
  (PC Gamer, "So we're all save scumming in Baldur's Gate 3, right?"; Prima Games, "Should you Save
  Scum in Baldur's Gate 3?"; Twinfinite's "what to do when you fail a roll" lists reloading); an
  early-access Steam thread says the seed is not stored, a reload gave a natural 20 after a 4.
  Against it, a Steam thread where a player reloaded a fight three times and got the same results
  (answered there as bad luck), and a question whether Honour Mode keeps the rolls of a save.

### What the mods do

- No study mod reads or writes the streams: `RollManager`, `BaseSeed`, `NumRolls` appear only in a
  copy of Script Extender's type declarations (Absolutes_Laboratory's IDE helpers; a search over
  the study mods' files). Wish: a mod that fixes or shows the dice ("predictable rolls",
  "dice log"), to read whether it writes the streams.

### What Script Extender declares, and what the game fills

- `eoc::roll::stream::RollManager` (`Ext.System.ServerRollSave.RollManager`) and the component
  `esv::roll::stream::StreamsComponent` (`ServerRollStreams`): `Streams`, `LastFrame`, `BaseSeed`,
  `RollModeType`; a `Stream` has `StreamType` (`Ext.Enums.RollStreamType`: Generic, Dialog,
  PlayerSpell, NPCSpell, PlayerDamage, NPCDamage, PlayerRandomCast, NPCRandomCast), `PlayerUuid`,
  `CombatGuid`, `CombatParticipants`, `RandomCastUuid`, `IsAlly` and a `PRNGState` of `Frame`,
  `FailDebt`, `SuccessDebt`, `Seed`, `NumRolls` (`gatekit symbols fields PRNGState`). Read through
  `GK.Rolls.Streams()`; the manager and the component held the same values in every reading.
- Read live: `BaseSeed` after a load is a clock in seconds (it equals the host's Unix time at the
  load; two loads 13.3 s apart had base seeds 13 apart). The generic stream's `Seed` is `BaseSeed + 1`
  and its `NumRolls` 0; every d20 roll adds 2 to it (the second die, section 3). A `Dialog` stream
  of the host appears with the first dialogue check, its seed `BaseSeed + 2` (seen once, a probe).
- A cold start of the level (`up --level`, no save): `BaseSeed` is the clock, but the generic
  stream's `Seed` is 1 and its count 0 - which is why the first checks of every cold start are the
  same dice (9/11 11/9 5/17 9/1 9/4 17/9 18/20 1/4, a third time in a probe).

### Hypotheses and whether each held

| Hypothesis | Source | Held? |
|---|---|---|
| The save keeps the roll streams, so the roll after a load repeats the one made after the save | a Steam report (the same results three reloads in a row); the wiki's Bestowed Bounty note | **did not hold**: after a load the generic stream counts from 0 with a new seed, and the checks differ (`save/load`) |
| A load draws fresh, so reloading throws a failed check again | most players; a Steam thread | held, and why: the load seeds the streams from a clock in seconds - two loads of one save draw differently (`save/load`) |
| The dice are a function of a stream's seed and count | the counter-like `PRNGState` | not tested: rung 5 writes seed and count back |

### Open

- Whether the save holds the streams at all (the load overwrites whatever it holds): no command
  reads a save's components - a wish for `gatekit save` (LSLib reads the `.lsv`).
- Two loads in the same second would draw the same dice, by the rule above; not shown (a load takes
  more than ten seconds).
- Karmic Dice's debt is a stream's (`FailDebt`, `SuccessDebt`); a load makes the streams anew, so by
  the rule it forgives the debt - not tested with Karmic Dice on.
- The Bestowed Bounty's items tied to the save are another random source than the roll streams.

## 5. The streams, written

- The game draws from the roll streams component: the roll manager's streams are overwritten from
  it every frame (section 6), so `GK.Rolls.SetStreams` writes the component; a write to the
  manager is lost.
- A die is a function of its stream's seed and count (section 6, the draw): a stream written back
  repeats its dice exactly (40 checks of 40); a count written ahead jumps the sequence; a count
  held (`GK.Memory.Freeze`) gives every roll the same pair.
- Every roller's passive checks share the Generic stream; a dialogue check draws its dice from the
  Dialog stream of the player and two more from Generic.
- `tests/rolls/streams/rewind.lua` shows the rewind: the Generic stream written back gives the
  same six checks' dice; without it they go on.
- Open: the fight's streams (Spell, Damage, RandomCast).

## 6. The code

What each instrument gave, with addresses: the chronicle,
[reports/rolls.md](../proof-of-concept/reports/rolls.md), rung 6. In short (`bg3_dx11.exe`):

| What | Where | Evidence |
|---|---|---|
| One number drawn: the generator put at the stream's count, the count increased, min + value modulo the range | +0x105AAB0 | `GK.Memory.Watch` on the manager's count: two hits per check at +0x105AAE3 |
| One stream copied, field by field (Script Extender's `Stream` layout holds) | +0x105DB40 | the watch's hits on the copies |
| The component copied into the manager every frame | `SystemUpdate<esv::roll::stream::UpdateSystem>` +0x238A9D0 | a hit at +0x105DBBD every frame from +0x238ABC3 |
| The manager copied into the component when it changed (`HasChanges`, +0x2A), and `BaseSeed` = the time in seconds when the component is made | +0x342D9D0, from `SystemUpdate<...::SaveSystem>` | a hit from +0x23993CC; the live base seed is the Unix time of the load |
| A pin's minimum and maximum applied to each die after the draw; the kept die chosen after the clamp | `ApplyRollResultBoosts` +0x105AD10, `RollDiceWithBoosts` +0x105CCA0 | a watch with a pin on: both dice drawn, the check came at the pin |
| A stream made: its id the manager's last id plus one, its seed the base seed plus that id, its count 0; `HasChanges` set. Generic is base + 1 as the first stream after a load | `RollManager::CreateStream` +0x309FBB0 (the Dialog stream through `GetDialogStream` +0x309F570) | a watch on the manager's last id over the first dialogue check: 1 -> 2 at +0x309FD9E; the Dialog stream then read seed base + 2 |
| An array of streams copied (clear, then each stream): the component into the manager every frame, the manager into the component on a change | `CopyStreams` +0x105D8D0 | a watch on the manager's stream count: 1 -> 0, 0 -> 1 each frame |
| The requested roll rolled on the server: the rolled record copied into the request, the verdict decided (natural 20 succeeds, natural 1 fails, else the difficulty at most the total) and written at the request's +0x4C with the natural die (+0x42) and the discarded die (+0x43, only when advantage and disadvantage differ) | `esv::RollSystem::RollDice` +0x32955C0, from `SystemUpdate<esv::RollSystem>` | a watch armed on a new request: +0x42 written at +0x3296664, +0x4C 1 at +0x3296682 on a pass, read 0 on a fail |
| The kept die's way from `RollDiceWithBoosts` to the request: +0x105B800, +0x1075BA0, +0x11888B0 (the evaluation of the request's stats expression), then a results array copied into the request by `CopyRollAndResult` +0xF18850 / `CopyStatsRollResult` +0xE75F60 | the three middle functions not read far enough to name | the draw's return chain (six entries); the Result's writes at +0xE75F73, +0xE75F7F |

The fields read so far are named in the records: functions in `research/coverage/functions.jsonl`
(`gatekit decomp names roll`), the stream, the manager and the draw's generator in
`research/coverage/structs.jsonl` (`gatekit decomp struct`), the components' through `names set`.

**Against Script Extender's declarations** (`Components/Roll.h`, `Hit.h`):
- `RequestedRollComponent.SpellCheck` (+0x4C) holds the roll's verdict, 1 on a success, 0 on a
  failure (watched and read above); named `Success`.
- `PRNGState.Frame` (+0x58 of a stream) is the stream's id, and the manager's and the
  component's `LastFrame` the last id given (CreateStream); named `Id` and `LastStreamId`.
- `Stream.Seed`, `NumRolls`, `StreamType`, `PlayerUuid`, the manager's `Streams`, `BaseSeed`,
  `HasChanges`, the request's `NaturalRoll`, `DiscardedDiceTotal`, `Result.Total`,
  `Result.NaturalRoll`: hold. `gatekit names extender --ns esv::roll` and `--ns
  eoc::RequestedRoll` list nothing (no name of those types waits at rung unknown).

## 7. The tracer

Script Extender's entity tracer (`Ext.Entity`, server or client, developer mode) logs which
components were created, destroyed, replicated or changed. GateKit wraps none of it; it is called
through `gatekit --instance <copy> exec server "<lua>"`. Its options are declared in the fork
(`Lua/Libs/Entity.inl`, `GameDefinitions/EntitySystemHelpers.h`).

- `Ext.Entity.SetupTracing{ TrackECB, TrackImmediateWorldCache, TrackReplication,
  TrackModifications, ExcludeModificationComponents }` - a table is required (`{}` takes the
  defaults: the first three on, modifications off). `TrackModifications` logs a changed
  component; `ExcludeModificationComponents` lists components by their Lua name
  (`ServerRollStreams`) whose changes are not logged - for one written every frame, which
  otherwise hides its own change in a subtraction from an idle baseline. A one-frame component
  cannot be excluded.
- `Ext.Entity.ClearTrace()`, `Ext.Entity.EnableTracing(true)`, the action,
  `EnableTracing(false)`, then `Ext.Entity.GetTrace()`.
- The trace: `Entities`, a map from the **entity** (the key, an entity object: resolvable while it
  lives, `[DEAD]` after) to `{ Components = { [index] = { Name, Frame, Flush } } }`; the
  trace's own `Frame` and `Flush` are running counters of ECS frames and command-buffer flushes.
  A component's `Frame`/`Flush` are those of its first change since the clear: **the order of
  changes reads by frame and flush, not inside one flush** (a passive check: the request at
  frame 4418 flush 4419, the finished event at 4431, the analytics roll at 4432).
- Not readable in the extender build GateKit runs: a component's `Flags` and an entity log's
  `Entity` and `Flags` ("property does not exist"; the fork's source declares them, the build is
  older). Take the entity from the map's key.
