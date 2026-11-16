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

## 8. Who asks for a die

Read in the code and, where a row says so, watched (`GK.Memory.Watch` on the manager's count of a
stream, the changing writes kept each tick with their return chains).

### The thrower

There is no single function every die passes; there are four that end a roll, and one that draws
nearly every die:

| What | Code | Seen |
|---|---|---|
| One whole number at the stream's count | `code:eoc::roll::stream::DrawNumber` | watched |
| The dice of one formula, no roller | `code:eoc::roll::stream::RollManager::InternalRollDice` | watched |
| A formula for a roller, with its boosts | `code:eoc::roll::stream::RollManager::InternalRollDiceWithBoosts` | watched |
| The same against a difficulty, on a stream named by id | `code:eoc::roll::stream::RollManager::RollDiceWithBoostsAgainstDC` | watched |
| A fraction at the stream's count, for the weighted decision | `code:eoc::roll::stream::DrawFloat` | read |
| A roll whose outcome is decided beforehand | `code:eoc::roll::stream::RollManager::RollDiceGuaranteed` | read |
| Dice thrown again by a reroll boost, by an interrupt | `code:eoc::roll::stream::RerollDiceInternal`, `code:eoc::roll::stream::RollManager::RerollDice` | read |

The generator is the standard 64-bit Mersenne Twister, seeded with the stream's seed for each
call of the dice roll (`code:std::mt19937_64::discard`, `code:std::mt19937_64::operator()`). A die
is drawn by discarding as many outputs as the stream's count, adding one to the count and taking
the next output modulo the faces. The generator is not seeded again between the dice of one call,
so with a count `c` at the start the die number `k` (from 0) is the output at
`(k + 1) * c + k * (k + 3) / 2`: the pair of a d20 roll is the outputs `c` and `2c + 2`. A model
of this gives the Generic stream's pairs at seed 1 (9/11 11/9 5/17 9/1 9/4 17/9 18/20 1/4 for the
counts 0 to 14), and four passive checks whose count was written beforehand rolled the dice
computed: 13, 4, 20, 1. Without advantage or disadvantage the first die of the pair is kept.

### The stream types

0 Generic, 1 Dialog, 2 PlayerSpell, 3 NPCSpell, 4 PlayerRandomCast, 5 NPCRandomCast, 6
PlayerDamage, 7 NPCDamage: read in the code's stream getters and seen in the manager's streams of
a fight (2, 6, 7, 3 in the order they were made); Script Extender's names for them agree. A
stream is found by its id (+0x58), not by a player.

### Who asks

| The roll | Asked by | Stream | Seen |
|---|---|---|---|
| A weapon attack against armour class | `code:unplaced::RollAttack` -> `code:unplaced::RollAttackAgainstArmourClass` | the attacker's, from the roll stream provider; PlayerSpell for the player | watched |
| A spell attack, an unarmed attack | `code:unplaced::RollSpellAttackAgainstArmourClass`, `code:unplaced::RollUnarmedAttackAgainstArmourClass` | as above | read |
| A saving throw | `code:unplaced::RollSavingThrow` | the provider's, for the condition's context | read |
| A skill check | `code:unplaced::RollSkillCheck` | given by the caller; Generic for a passive check | watched (passive) |
| An ability check | `eoc::stats::AbilityCheck` | given by the caller | read |
| A roll written in a stats expression | +0x1187D40, +0x1185A70, +0x11934E0 | Generic, or the caller's for damage | read |
| Damage dice of a hit | `code:unplaced::RollDamageDice` from the deal-damage functor | the damage stream | watched |
| A damage estimate over an entity's spells | +0x41C4D00, and the AI's scoring of functors | Generic | read |
| The dodge roll of an attack's target | `code:unplaced::RollDodgeAttack` | the target's | read |
| A heal | `code:eoc::StatusPrototype::GetHealAmount` | Generic | read |
| Initiative | `esv::combat::SystemHelper::RollDie` (+0x32019A0) | Generic, one die, no boosts | read; the Generic count rose by 3 as a fight of three began |
| A spell's recharge | +0x339C2C0 | Generic | read |
| A random cast: the check, the pick of the spell | +0x2E92110, +0x2E936C0 | the random cast stream; Generic | read |
| A script's `Roll` | +0x10F08A0 | Generic | read |

A weapon attack by the player that hit moved PlayerSpell by 2 and PlayerDamage by 1; the damage
die of that attack returned through +0x36EC3FC and +0x36E9347.

### What does not pass the streams

`code:unplaced::RepickGuaranteedOutcome` (and the same lines inside the weapon and spell attack
rolls) replaces the natural die of a roller with a guaranteed-chance-roll-outcome boost by a
number of the C runtime's generator. A stats functor's chance to apply is that generator too
(+0x36D9DC0, +0x36DD2E0, +0x36E2970, +0x36D68D0). Read, not watched.

### Karmic Dice

Read in `code:eoc::roll::stream::RollManager::CheckStreamAgainstDC` and the debt lines of the
roll with boosts; nothing of it is watched yet.

- It acts only in the two rolls against a difficulty, only when the manager's mode is weighted,
  and never on the Generic and the two damage streams.
- A stream keeps two debts. A bad outcome adds the chance of success to the success debt; a good
  outcome adds one less that chance to the fail debt, except on Dialog, PlayerSpell and NPCSpell,
  whose fail debt never grows.
- Before a roll, a stream that owes a success draws one fraction and may force the wanted
  outcome, the chance rising with the debt divided by the type's range
  (`RollStream<Type>DebtRange`); otherwise a stream that owes a failure may force the opposite.
  A used debt falls by a multiple of the chance.
- A forced roll takes its natural die uniformly among the faces that reach (or miss) the
  difficulty and reads none of the roller's boosts: a pin's minimum and maximum, Reliable Talent
  and Halfling Luck do not act on it. This is why a pinned dialogue check came at another number.
- On the damage streams the weighted mode makes each die a rounded draw of a normal distribution.

### One throw among several

Two ways, both shown live.

By the stream's count (`GK.Rolls.SetNext`, any Script Extender build of ours): the next roll of
one stream takes a chosen die; `test:rolls/streams/chosen_die` shows it on a passive check and on
two fair attacks. The rolls after it on the same stream draw from where the count is left, so
they are not what they would have been.

By interception (the fork's build with roll events, developer mode): the four functions of the
thrower are wrapped and each call is an event in server Lua carrying the asking code's return
address, the stream, the roller, the roll and the result, the result writable. Four passive
checks asked in one tick rolled 9, 11, 5, 9; the same four from the same counts with the third
roll's natural die written as 2 rolled 9, 11, 2, 9. The asking addresses seen are the ones the
watches gave: a passive check from inside `code:unplaced::RollSkillCheck`, a weapon attack from
inside `code:unplaced::RollAttackAgainstArmourClass`, weapon damage from inside
`code:unplaced::RollDamageDice`. Replacing the roller of a check with another creature did not
stop the game; the bonus stayed the first roller's, since the caller computes it before the
throw, and whether the other creature's boosts act is not seen.

### The combat log's own account

Read in the game's names; nothing of it is watched yet. The log is not written by the thrower.
`esv::combat_log::CombatLogSystem` builds typed entries in its own passes, each from the records
another system left: `ProcessHitEvents` (a `HitEntry`, made from the hit's description - which
carries the attack roll and the saves in its condition rolls), `ProcessSkillCheckRequests` (a
`RollFinishedEntry`), `ProcessRandomCastRequests`, `ProcessHealedRequests`,
`ProcessInterruptsUsedRequests`, `ProcessStatusAddedRequests`, `ProcessStealthSpottedRequests`
and others, and sends them to the clients. So a roll is in the log when it ends as a hit or as a
finished roll that a request names, and a roll that ends as neither - a die of a stats
expression, such as the Perception roll that sets a Stealth check's difficulty - has no entry to
appear in. The entry types: AutomaticDialog, CampSuppliesUsed, CombatInitiated, CombatJoined,
CombatLeft, CombatMultipleEncounters, CombatNextRound, ConcentrationCleared, Death, DialogStart,
EntityDropped, EntityPickup, EquipStatus, Healed, Hit, Inspiration, Interrupt, ItemConsume,
ItemLockpick, LevelChange, Ping, RandomCast, Resurrect, RollFinished, Spell, StatusAdded,
StatusRemoved.

## 9. The rolls watched one by one

Each row was seen once in the roll log (the family `dice`) on a staged scene: the host, a goblin
kept alive, spells taught for the scene. `test:` names none of them yet.

| The roll | Stream | Asked by | What was seen |
|---|---|---|---|
| A spell attack by the player | PlayerSpell, a pair | `code:unplaced::RollSpellAttackAgainstArmourClass` (+0x106DF78) | a miss throws no damage dice; a hit throws them on PlayerDamage |
| An unarmed attack by the player | PlayerSpell | `code:unplaced::RollUnarmedAttackAgainstArmourClass` (+0x106EFD9) | roll type MeleeUnarmedAttack; no damage roll |
| A target's save against the player's spell | PlayerSpell: the caster's stream, the roller the target | `code:unplaced::RollSavingThrow` (+0x10707A0) | Sacred Flame, Hold Person |
| A target's save against an NPC's spell, outside a fight | Generic | the same | outside a fight there are no NPC streams; an NPC's damage is on Generic too |
| A status's repeating save | Generic | the same, reached through the status system | an NPC at the end of its turn; the player at once when the status is applied and at the end of each turn of his |
| A concentration save | Generic | `code:unplaced::RollExpressionAgainstDifficulty` | difficulty 10; on a failure the spell ended |
| A save with advantage from Gnome Cunning | as the save | | the higher die kept; the chance passed down is the caster's chance squared |
| Scorching Ray | PlayerSpell | | three attack rolls in one cast, damage dice only for the ray that hit |
| Magic Missile | PlayerDamage | | no attack roll; three separate rolls of one die |
| A dialogue check | Generic, then Dialog | `code:unplaced::RollExpressionAgainstDifficulty`, then `code:unplaced::RollSkillCheck` | as the roll window opens the check is rolled once as a stats expression on Generic; the throw itself is on the Dialog stream, made as the window opens, and is the one that counts. This is the pair of Generic dice a dialogue check draws |
| An invisible creature against one with See Invisibility, outside a fight | Generic | through the status system | the invisible creature makes a Dexterity save from the status `SEE_INVISIBILITY_SEEN`; on a failure its invisibility is taken off |

A weapon hit by an NPC throws two rolls of damage dice through two call paths and deals the first;
a miss throws one. What the second is for is not known.

### A story's cast makes no roll

`Osi.UseSpell` casts with the options that ignore spell rolls, cast checks and target checks: a
Fire Bolt ordered so hit with damage dice and no attack roll, and an NPC's Sacred Flame asked no
save. `GK.Cheat.Cast` takes those options off and the rolls are made.

### Karmic Dice, watched

With weighted rolls on (`GK.Rolls.SetKarmic(true)`), on the PlayerSpell and NPCSpell streams:

- a failed roll adds the roll's chance of success to the stream's success debt;
- a forced success lowers the debt by about twice one less that chance, below zero;
- a positive debt does not always force: with a debt of 0.2 a roll was still thrown and missed;
- a forced roll draws no dice of its own formula, and the stream's count rises by 3 where a
  thrown roll raises it by 2 (the fraction of the decision, and the two dice of the forced roll);
- a forced roll ignores a pin: with the attack die pinned at 1, forced rolls came at 14 and 13
  and hit;
- NPC streams are weighted as the player's are.

### Not staged

A death save (the level has one party member, and the last one down ends the game); a spell save
asked by an NPC inside a fight; Extra Attack; See Invisibility inside a fight; the shape of
damage dice under weighted rolls; a guaranteed-chance-roll-outcome boost and a functor's chance,
the randomness outside the streams.

## 10. What the watched rolls mean

### The stream belongs to the side that began the roll

A target's save against the player's spell is drawn from the player's PlayerSpell stream, the
roller being the target; the same save asked by a status at the end of a turn, with no caster
acting, is drawn from Generic. So a stream is not "whose die" but "whose action": the player's
attack rolls and his targets' saves against him share one stream and one pair of debts. This is
what bg3.wiki describes from outside as a debt kept for a side, and a save that counts for the one
who caused it. For choosing a die it means `GK.Rolls.SetNext` on PlayerSpell decides the next roll
of that stream whoever throws it - the player's attack or the goblin's save.

### The dialogue check's two dice on Generic

When a roll window opens the check is evaluated once as a stats expression, with real dice from
Generic, before the throw the player makes on the Dialog stream. The first result is not the one
shown or used for the verdict. What it is for is not read; the pair is drawn every time, so a
dialogue check moves the Generic stream as a passive check would.

### A cast ordered by the story

`Osi.UseSpell` is not a way to stage a roll: it casts with the rolls ignored. A scene that needs
the attack roll or the save uses `GK.Cheat.Cast`, or the player's own input.

### `once` and a spell's save

A pin with `once` is taken off at the holder's next finished-roll event of the kind. A save asked
by a spell of its target raises no such event (it lives in the hit), so the pin stays and decides
the next save of that holder instead - seen on Hold Person, whose first save went unpinned-for
and whose repeat at the end of the turn was the one the pin was taken off by. Suspected a defect
of ours, not of the game: with the roll events the pin can be taken off at the throw itself.

### Open after the watching

- The second roll of damage dice an NPC's weapon hit throws, by another call path, not dealt.
- The stats-expression roll of the dialogue check: what reads its result.
- A death save, Extra Attack, a spell save asked by an NPC in a fight, See Invisibility in a
  fight, the damage dice's shape under weighted rolls, the randomness outside the streams.

## 11. The pin, judged

Three ways to decide a die now exist. What each binds:

| | The pin (`GK.Rolls.Pin`) | The stream's count (`GK.Rolls.SetNext`) | The roll events (the fork's build) |
|---|---|---|---|
| By what | the game's own boost pair on the roller | the stream's count, written | the result written at the throw |
| Binds | every roll of one creature and one kind, for as long as it is on | the next roll of one stream, whoever throws it | any roll a condition on the event picks |
| A single throw among several | no | only the next of its stream; the ones after move | yes: four checks in a tick, the third changed |
| By target, by damage type, the n-th hit | no | no | the roll's type and order; the target only where the asking code is known |
| Under Karmic Dice | a forced roll ignores it, so the pin switches Karmic Dice off | a forced roll draws other dice | decides the result whatever forced it |
| Delay | about three ticks to arrive, three to four to leave | none | none |
| Needs | any build, developer mode not needed for the boost | any build of ours | the build with roll events, developer mode |
| What it is to the game | a boost a mod could give: the game's own rule | a write to saved state | an intervention in the code |

The owner's two examples. "Every attack roll of ours is 20" is the pin as it is, with Karmic Dice
off. "Every fire damage roll on X gives 1" is not: the pin has no target and no damage type. But
the game's own boosts take a condition - Elemental Adept is
`IF(MainDamageTypeIs(DamageType.Fire)):MinimumRollResult(Damage,2)` - so a pin given a condition
in the game's own words reaches the damage type and whatever else a boost's condition can read,
still with no fork.

The judgement: the pin stays, for what it alone does - a standing rule, in the game's own terms,
on any build - and is reshaped in two places: it takes an optional condition written as the
game's boosts write theirs, and `once` is taken off at the throw where the roll events are at
hand. Deciding one throw is not the pin's work: `SetNext` does it on any build for the next roll
of a stream, and a verb on the roll events does it exactly where that build runs. Proposed, not
built: the condition on the pin, `once` at the throw, and the verb on the roll events.

## 12. What the mods do with a roll

Read, never run. The copies are in the home's study mods.

| Mod | What it does to a roll | By what |
|---|---|---|
| Loaded Dice (Nexus 10607) | checks at 20, or at 1, by a toggle | a status whose boosts are `IF(RAW_IsSkillCheck(context.Source)):MinimumRollResult(SkillCheck,20);...MaximumRollResult(SkillCheck,20)`, the conditions in a script of the game's condition language; its Lua only gives the passives again when they are lost |
| Manual Dice Roll (mod.io) | the player types the die he threw at the table | one boost text `MinimumRollResult(X,N);MaximumRollResult(X,N)` for attack, ability, skill, save and damage, put on every party member with `Osi.AddBoosts` and taken off a tenth of a second after `AttackedBy`; it watches `RequestedRoll` only to log, and says itself that it does not see attack rolls there |
| Damage Preview 2 (Nexus 4137) | shows the hit chance and the damage range beside the cursor | no Lua at all: one interface page bound to values the game's own view model already holds (the damage range with resistances, the list of damage rolls, the total hit chance, the counts of advantage and disadvantage) |
| Improved Spell and Damage Preview (Nexus 22672) | computes the same preview by itself | a copy of the game's rules in Lua: the condition language, the functors, rerolls and bounds on a die, damage as a whole probability distribution; a request to the server on every change of target, answered when the server's cast exists |

What follows from them:

- Every mod that decides a die uses the boost pair, as the pin does; none reads the roll
  streams, and none can see or change one throw. A mod that wants "this roll" re-adds and removes
  boosts on a timer and hopes the roll falls between.
- A boost's condition in the game's own words is how a mod narrows a rule today (a kind of check,
  a damage type), which is the condition proposed for the pin.
- The game computes a preview of an attack on the client for its own interface - the hit chance
  in the targeting information of the spell task, the damage range with resistances in the
  view model of the target under the cursor. One mod shows those values; the other recomputes
  them in Lua and pays for it with a server round trip, the conditions compiled anew at each
  check and a search of every creature around for interrupts. Neither can ask the game for the
  answer for a caster, a spell and a target of its choosing: that question has no call.
- Users of the first report that the text at the cursor lags behind over creatures and that it
  grows worse as a game goes on; since the mod only shows what the engine gives, the delay is in
  how the engine refreshes that view model. Not measured.

## 13. The game's own preview of an attack

Read and probed (the host aiming `Target_MainHandAttack` at a goblin for five seconds, the cursor
still; places are of `bg3_dx11.exe`, the build the registry holds).

- The path, on every frame a spell is aimed: `ecl::InputController::UpdatePreview` ->
  `ecl::CharacterTask_Spell::UpdatePreview` -> `SetCursor` -> `GetTaskInfo`, which fills the
  targeting information, -> the listeners of the interface's targeting feedback, one of which is
  `gui::ViewModelProvider::OnUpdateOverheadTarget`. Each ran once a frame (165-171 times in the
  five seconds at 30 frames a second) whether the target changed or not;
  `eoc::stats::ApplyResistance` and `eoc::StatsSystem::LEGACY_CreateStatsRollContext` ran twice
  a frame.
- The chance is `eoc::spell::CalculateModifiableSpellRollChance`: the spell's roll conditions, a
  context naming the caster and the target, the spell's name and the caster's modifications in,
  a number from 0 to 1 out. The targeting information keeps the chance without advantage and the
  shown chance held between 1 and 99.
- The damage is the tooltip's damage rolls resolved
  (`gui::statsDescriptionHelper::ResolveStatsTooltipDamages`) with the resistance applied to the
  least and the most of each.
- It agrees with the dice: the preview said 65% and 4-7 piercing; the roll that followed had a
  difficulty of 13 and a bonus of 5 (13 faces of 20 hit) and dealt 7.
- The combat AI asks the same function for its chance (26 times in an enemy's turn, from its
  scorers) but estimates damage by another one (`unplaced::EstimateSpellDamage`, 154 times in
  the turn, through the average-damage cache's miss). Neither draws from a roll stream: the
  Generic stream's count was the same before and after two enemies' turns.
- So the question "what are the chance and the damage for this caster, this spell, this target"
  has a function in the game that takes all three as arguments. It is not offered to Lua. A
  call of it needs the context built as the game builds it; that is the tool a preview mod
  lacks, and it is not built yet.
- Why the cursor text lags is not measured. What is known: all of it is computed anew every
  frame, the conditions evaluated and the collections of the view model rebuilt; that the cost
  grows with the boosts, statuses and passives the conditions walk is a guess.

Not yet named: the methods of the AI's score object that ran only in an enemy's turn, the
constructors of the context, and the caller of two Generic rolls of no dice seen in a fight.

## 14. The combat log's account, read and probed

- One update a frame, `esv::combat_log::CombatLogSystem::Update`, runs about twenty passes; each
  reads one kind of event component and turns it into typed entries. The entries leave through
  `unplaced::CombatLogProtocolAddEntries`, which hands each to a table of 31 slots, one per type
  of entry. A probe on a slot counts the entries of that type sent: "was this logged" for any
  roll.
- A hit, a miss, a spell's save and a status's repeated save all pass `ProcessHitEvents` and
  become one hit entry built from the hit's result event: the roller is the hit's inflicter, the
  natural die, total, critical, difficulty and terms are the hit's condition rolls, the damage is
  the hit's own account. A miss and a save that took no harm are hits all the same.
- A check asked by the story and a Stealth check pass `ProcessSkillCheckRequests` and become a
  roll-finished entry built from the skill-check event.
- The "hidden Perception roll" beside a Stealth check was not there in the scene staged: the
  Stealth check's difficulty was the watcher's passive Perception, and no Perception die was thrown.
- What has no entry, and why:
  - damage dice - folded into the hit entry as totals;
  - a roll of no dice from an expression (seen after nearly every cast) - no pass reads it;
  - a failed Perception, Survival or Investigation check when the ruleset modifier
    `HIDE_FAILED_PERCEPTION_ROLLS` is on (watched both ways); an event marked for the modifier
    `DIALOGUE_PASSIVE_ROLL_HIDE` (read only);
  - a spell with the flag `CombatLogSetSingleLineRoll`, a status with `DisableCombatlog` (read only);
  - a hit whose target is invisible and no player, or where neither side is a player, near a
    player or in a fight with one (`EntitiesMeritLogEntry`).
- The request's "from a script" mark and its context play no part in any pass read.
- Open: one failed Stealth roll in an enemy's turn had no entry and did not happen again.

## 15. What a creature is about to do, before the die

The first thing watched that knows "this creature, this action, this target" is the making of
the cast's entity: its `SpellCastState` (caster, spell, targets, position, the cast's own random
seed) appears about a tenth of a second before the attack roll, for a player and for the AI
alike. A mod can listen to it today (`Ext.Entity.OnCreate("SpellCastState")`). The step between
the AI's chosen action and the cast's start request is not placed.

## 16. A die changed after the throw

An interrupt whose property is `SetRoll(n)` (the game's own: Portent, Shield against Magic
Missile, Illusory Self, Critical Execution) changes the natural die after the roll manager has
returned: the roll event and the roll log said natural 19, total 24; the hit's condition roll
and the combat log ended with natural 1 and a miss. A rig at the throw is overridden the same
way. It passes `esv::interrupt::EventSystem::StartInterruptEvent`; neither the reroll functions
of the roll manager nor a conditional roll adjustment ran. The function that writes the new die
is not placed yet.

So the roll log says what was thrown, and the game may go on with another number. Until the
writer is found the row cannot say so: this is the first thing owed to the log.

## 17. What a roll is for, readable at the moment of the roll

Probed with the frames still open while the roll event runs.

- Every roll a stats condition asks passes `unplaced::EvaluateStatsCondition` with the
  condition's context as its fourth argument (`ls::thoth::shared::ContextData`, by the name the
  symbol builds give it). In the context: the source at +0x00 (the caster, the attacker, a
  status's cause), the target at +0x10, a pointer to the hit's description at +0x68 whose +0x150
  is the spell's name, and at +0xD4 the status's name (none for a spell's roll). Seen open at
  Sacred Flame's save, Fire Bolt's attack, a creature's weapon attack and Hold Person's repeated
  save.
- A spell's roll passes `unplaced::EvaluateSpellRollCondition` first, with the same context as
  its second argument and the spell's name behind its third.
- Damage dice, a heal and the rolls of no dice are never inside a condition: they run inside the
  execution of a stats functor, which the Script Extender already wraps for its own events. At
  the roll the functor's context gave: for the first damage row of a weapon hit - the attack on a
  target, the functor list of the spell's success, caster, target and spell; for the second -
  the attack at a position, the spell's own properties, no target; for a potion's heal - caster
  and target the drinker, the status `POTION_OF_HEALING`, the functor list of its application.
- Initiative: `esv::combat::System::NewInitiativeRoll` holds the combatant.
- So "who, against whom, by which spell or status" is there at the roll for a condition's roll
  and for a functor's roll alike, from two places the extender can stand at: the condition
  evaluator and its own functor wrapper. Not yet built into the roll event.
- Fall damage has no dice: a function of the distance fallen (the symbol builds name it; not
  placed in this build). Sleep rolls nothing: a fixed budget of hit points by the wiki.

Open: what the rolls of no dice evaluate; the second damage row's part in the hit points lost.

## 18. After the throw: what an interrupt does to a roll

Probed in order of time, the roll event placed between the probes.

- Every change of a roll by an interrupt passes one function,
  `esv::interrupt::AdjustConditionRoll`: its first argument is the adjustments resolved for the
  roll, its second the condition roll (the roll's result, its difficulty, the roll's id); it
  answers whether success flipped and what changed. Seen for the game's own Critical Execution
  (`SetRoll(20)`: 15/20 became 20/25, a critical), a `SetRoll(1)` (17/22 became 1/6, a critical
  failure), Lucky's reroll (3/8 became 11/16) and Bardic Inspiration on a save (total 7 became 8,
  the natural die as it was).
- The new value is worked out in `esv::interrupt::ResolveRollAdjustmentsForRoll`: a set roll is
  the fixed value with adjustments and bonus, an adjustment is added, a reroll is a call of the
  roll manager's `RerollDice` - the first time that function was seen running.
- The order for one attack: the roll event; in the same tick the interrupt is used and its
  functors run (Bardic's d6 is thrown here); 0.43 s later, in a later pass of the cast, the
  change is written; then the combat log's interrupt entry; the damage dice; the hit entry.
  When the player is asked, the cast waits at the reaction window and nothing is written until
  the answer.
- So the roll event always comes before any interrupt, and the roll log's row is what was
  thrown. A reroll by an interrupt throws no new roll event. Which interrupt did it is not among
  the function's arguments; the game's own log reads it from a component of the same tick.
- A reroll that is a boost (Halfling Luck, Great Weapon Fighting) is no interrupt: it happens
  inside the throw, before the event, and the row's dice show every draw (1, 1, 15, 10).
- The final truth of an attack or a save is the condition roll of the cast, which becomes the
  hit's.

Rolls watched for the first time: a creature's spell forcing a save on the player (the
creature's stream); a concentration check (Generic, the request's context 7; a failure shows in
the combat log as the status removed); Extra Attack (two casts, an attack roll each); an
off-hand attack (its own roll type, damage with no ability bonus); a forced outcome of Karmic
Dice after two misses (the row has no dice at all); damage dice with Karmic Dice on (the dice
were not the generator's own: one sample). On twelve checks of the Generic stream with Karmic
Dice on the weighted path never ran.

Not staged: a death saving throw. Every way tried of downing a party member in the test level
ended the game at once or killed the creature outright; a save with a real companion is needed.

## 19. Asking the game for a chance: the recipe

Read from the server's own caller (an AI scorer) and watched on it. Not built yet.

- No function of the game takes just a caster, a spell and a target. The one to call is
  `eoc::spell::CalculateModifiableSpellRollChance`: the spell's roll condition, a condition
  context, the spell's name, the caster's spell modifications (may be none), the stats and the
  server's condition machine in; a chance from 0 to 1 out, advantage folded in. When the caster
  has no modifications `eoc::CalculateStaticSpellConditionChance` gives the same number.
- The condition comes from the spell's prototype (`eoc::SpellPrototype::GetSpellConditions`,
  the kind "spell roll", by the spell's text key). The context is built as the scorer builds
  it: the caster, the target, their positions, a fresh hit description and attack description,
  no status. The machine is a field of the server's object.
- Watched: a goblin's weapon attack 0.5 with a difficulty of 14 and a bonus of 3 (ten faces of
  twenty), its Shove 0.3; with advantage on its attack rolls 0.75, and the roll showed advantage.
- The server has no cheap damage answer for a target: its estimates take the caster alone and
  no resistance (4 for 1d6+1: the average rounded down). The target-aware number is the
  client's preview.
- The roll of no dice seen after casts is the duration of a status that a weapon attack's own
  properties give to a creature that is no player (an AI helper for its extra attack). The
  "second damage row" of a weapon attack is a ground damage of the same properties.

## 20. The dice against the wiki: what agreed and what did not

A sweep of effects that use dice, each staged with the dice rigged where a number had to be
exact, the roll log beside the game's own account of the hit. Level 1 host, a goblin kept alive.

Agreed with the wiki and the data: the terms of an attack roll with Bless (the d4 in the terms
is the d4 thrown); Guidance on a check; a critical doubles the weapon's dice and the dice of
riders (Sneak Attack, a fire coating) and no flat bonus; Great Weapon Fighting rerolls a 1 or 2
once, riders' dice too, and keeps the reroll when it is lower; Halfling Luck; Savage Attacker
takes the higher of two throws die by die (the tooltip can be read otherwise); Elemental Adept;
advantage with disadvantage is one die (the first of the pair); half on a save rounds down;
vulnerability doubles; a potion of healing is 2d4+2.

Did not agree:

- **Sneak Attack's dice are not among the hit's damage rolls.** The spell deals
  `DealDamage(<the weapon's> + LevelMapValue(SneakAttack))`. On a rigged critical the hit's list
  of damage rolls held one piercing roll, 2d4+3 = 6; the sneak attack's 2d6 = 12 was in no roll
  and no modifier; the hit's total for piercing said 18. The combat log's line reads
  "damage roll: [1] = 18" with the left part filled from that list when hovered (not read:
  the view model holds it empty until then). This is a likely source of the reports of terms
  that do not add up to the total. Any dice inside a stats expression beside the weapon's roll
  should do the same.
- **Resistance does not bring damage below 1.** The wiki says halved, rounded down; 1 stayed 1
  (a Fire Bolt and a greatsword, each pinned to 1), 3 became 1.

Thrown and shown nowhere: the ground damage of every weapon attack, misses included (it never
took hit points and is never doubled); Halfling Luck's reroll of the discarded die of the pair;
the dice of Bless, Guidance and of every rider are drawn from the Generic stream, so Karmic
Dice never weights them.

Shown and not thrown: an attack forced by Karmic Dice shows a natural die and a discarded one
in the hit; no die was drawn. The combat log says a creature kept from dying "dies" when its
hit points reach 0.

Karmic Dice, watched: the success debt of the player's stream went 0, 0.35, 0.70 over two
misses, then a forced hit (the debt at -0.60 after); a forced hit also came at a debt of 0.10.

Not staged: Great Weapon Master and Sharpshooter (the passive stayed disabled), Bane on saves,
two sources of advantage, the damage rolls of a Sneak Attack that is no critical.

## 21. What a fight's event log tells, and what it does not

One small fight with every family on, each thing a fight consists of made to happen in turn,
the rows read against the game's own events and components.

Told in full: a melee, ranged and spell attack (hit, miss, critical), a save failed and made,
an area spell on two, a status ticking, hiding with its Stealth check, surprise.

Not told at all: a move (the game raises no event for it; what is left is the movement spent
and the new position); concentration starting; a creature joining a fight that goes on.

Told in part: the start of a fight (no list of who is in it; the initiative rows name no
creature), a turn (no round, no end), damage against a resistance (the halving is not said), a
heal (no healer, no amount before the cap), a status (no duration; why it left is not said), a
reaction (nothing says the attack was one), a bonus action (what it spent), a jump (no
destination), a drunk potion (no row of the item used), a kill (no killer), the end of a fight
(no reason).

For each the game holds the fact in an event of the story or in a component of one frame
(listed in the night's notes); none needs a function hooked.

What a creature is about to do: nothing says it before the cast's own state appears - 10 to 29
ticks after its turn starts, about 20 ticks before the attack roll. The move the AI is about to
make is inside that same state (where the caster stands and where it will cast from). The AI's
own planning components never appeared for a single enemy.

## 22. The chance, built and proven

`Ext.Stats.GetSpellRollChance(caster, spell, target)` in the fork calls the game's function by
the recipe of section 19, and `GK.Rolls.Chance` asks through it. Proven against the dice
(`tests/rolls/chance/agrees_with_dice`): 0.65 asked, and the attack then thrown had a
difficulty of 13 and a bonus of 5 - thirteen faces of twenty - for a melee and for a ranged
attack.

One thing a caller must know: the game works a weapon attack out with the weapons of the
caster's active weapon set, and switches the set only as an attack is really made. A melee
attack asked while the bow is in hand is counted without the melee weapon (0.45 for the same
attack: Strength in place of the dagger's Dexterity). The condition reads the set from the
caster's own component; nothing in the context changes it. `GK.Rolls.Chance` puts the right set
on for the length of the question and puts it back. Whether the game's AI weighs the wrong
number for a creature standing in its ranged set is being watched.

## 23. The second sweep

- **The rule behind the missing Sneak Attack dice.** Damage dice thrown for a stats expression
  never reach the hit's list of damage rolls; only the weapon's own dice do. Seen for Sneak
  Attack (also without a critical), Divine Smite (a hit of its own with an empty list), Sand
  Toss and Colossus Slayer. The last is the worst case: its hit lists a weapon roll, 1d4+3 = 4,
  which was thrown and is not the damage; it dealt 8, the d8. Dice that come from a boost
  (a damage bonus, Hunter's Mark, a fire coating) are listed with their roll and add up.
- **The game's AI weighs a melee attack with the weapon set in hand.** A goblin with a scimitar
  and a shortbow, standing in its ranged set: the scorer's number for its melee attack was
  0.2025 where the dice were 0.25. It chose the ranged attack. The bias is always against melee.
- **The wiki's list of bugged actions: eight of eight still true in this build** - Firewine
  Explosion's halved save, Cinderous Swipe in wild shape, Gargantuan Cleave's half damage,
  Divine Strike (fire, melee) with no damage type, Gossamer Tomb, Sand Toss using Strength with
  the ranged set in hand and taking Great Weapon Master's -5 with no +10, a damage bonus boost
  not doubled on a critical.
- Agreed: Great Weapon Master (it needs proficiency with the weapon), Sharpshooter, Bane's d4,
  two sources of advantage (still one extra die), Bardic Inspiration with Guidance.
- Thrown and shown nowhere: a whole roll of a dialogue check as its window opens; a second d20
  on every saving throw, kept as discarded. Shown and not thrown as shown: a damage roll's
  critical flag compares the sum of its dice with one die's size (3+3 on 2d6 is flagged).
- What the player sees in the combat log's tooltip was not read: the tooltip's lines are not
  filled by opening it from code.

## 24. What the player sees in the combat log

- The tooltip of a line of the combat log is built once, as the entry is made, by the client
  (`gui::CombatLogEntries::VMHitDamagedEntry::GenerateDamageTooltips`), from the entry the
  server sent. Its damage line is written from the hit's resolved stats expression, not from
  the hit's list of damage rolls. Hovering only shows what was built; no request is made.
- So the gap of the hit's list (sections 20, 23) is not what a player sees: the tooltip of a
  Sneak Attack reads "2 (1d4) + 3 + 5 (1d6) = 10" and adds up; Colossus Slayer's own line adds up.
- **What does not add up for the player: a damage die that lands on 1.** The tooltip then shows
  only the die - no modifier and no total - while the damage dealt has both: "1 (1d4)" for 4
  dealt, three times of three. The same roll on a 2 or a 4 shows "+ 3 ... = N". The branch that
  drops the terms is not found: in the building of the line, or already in what the server sends.
- The lines can be read from the view model's memory (developer mode); a verb for it is
  proposed. A forced opening of the tooltip shows an empty box, and nothing in the toolkit moves
  the game's pointer over a line.

## 25. A correction: the dice of a stats expression are in the hit

Sections 20 and 23 said that damage dice thrown for a stats expression (Sneak Attack, Divine
Smite) are not in the hit. They are - in another field. A hit's damage holds two things: the
list of damage rolls, which has the weapon's own dice only, and the damage's resolved stats
expression (the Script Extender names that field `ConditionRoll`, which misleads), with the
expression's text, its dice and its damage types. Sneak Attack used as an interrupt is a hit of
its own whose expression is `LevelMapValue(SneakAttack)` with 1d6, the same die the roll log
shows; Fire Bolt's list of damage rolls is empty and its d10 is in the expression. Section 24
agrees: the combat log's tooltip is built from that expression, which is why it adds up.

What stands of the earlier finding: the list of damage rolls alone is not the whole of a hit's
dice, and a reader of a hit must take both; Colossus Slayer's hit lists a weapon roll that is
not its damage. Whether any hit's damage is in neither field is open: the one case where the
sweep saw a total above everything listed (Sneak Attack cast as the spell) was not staged again.

The game's rows now carry both (`game_hit`: `rolls` with where each came from, `modifiers`,
and `unlisted` when the listed terms fall short of the total).

## 26. Why a damage die of 1 shows without its terms

The cause is on the client, in the function that writes one stats roll into a tooltip's line
(`gui::StatsExprStringBuilder::StatsRoll`). It writes the die, then looks at the roll's critical
flag: on a critical failure it writes nothing more - no flat bonus, no ability modifier. The
line's builder adds "= total" only when two terms or more were written, so the total goes with
them. The rule is the attack roll's - a natural 1 misses whatever the bonus, and its line reads
"1 (Critical!)" - and it is applied to every roll.

The game flags a damage roll a critical failure exactly when its dice sum to 1 (and a success
when they sum to the die's size or more), which only a roll of one die can do: a weapon's d4 or
d6 landing on 1. The weapon's ability modifier is inside that same roll, so it vanishes from the
line. A heal's die of 1 adds up, because its modifier is a roll of its own.

What the server holds and what it sends is whole: natural 1, total 4, the flat 3. Proven both
ways by changing the flag before the hit reaches the log: a die of 1 with the flag taken off
reads "1 (1d4) + 3 (Dexterity modifier) = 4"; a die of 4 with the flag put on reads "4 (1d4)".
The damage dealt is right throughout.

## 27. The faults held by tests, and two findings taken back

Seven tests under `tests/quirks` hold what the sweeps found, each as the game does it today:
resistance leaves 1 of a damage of 1 (and 1 of 3); a melee attack's chance and Sand Toss are
worked out with the weapon set in hand; a weapon attack that misses still throws its ground
damage and takes no hit points; a saving throw draws two d20 with neither advantage nor
disadvantage; a roll forced by Karmic Dice writes no dice of its own while its stream moves by
three draws and the hit shows a natural die; a damage roll's critical flag is of the sum of its
dice (2+2+2 on 3d6 is flagged, 1+1+1 is not); four entries of the wiki's list of bugged
actions read from the game's data.

Taken back:

- "A damage bonus from a boost is not doubled on a critical" did not reproduce: on a rigged
  natural 20 `DamageBonus(2d6)` threw four dice and the hit lists 4d6. What the second sweep
  read as "not doubled" was the ground row, which throws the boost's dice undoubled. The wiki's
  entry is about Arcane Shot, which a level 1 host cannot stage.
- "A forced hit shows a die that was never thrown" is said too strongly. The wiki says the game
  picks a d20 value among those that would have succeeded; the forced roll moves its stream by
  three draws, so the die shown is most likely drawn there, on a path the roll event does not
  see. What holds: the row of the forced roll has no dice.

Seen once and not again: in a fresh game a forced roll lowered the debt and the hit still said
"miss".

## 28. What five more mods would gain, and what they still lack

Read, never run: a trader-inventory cleaner, a knock-out mod, a modder's own combat logger, a
re-layout of the combat log, and a mod that punishes a critical miss.

- The modder's combat logger (2.4 thousand lines of Lua, written to settle the same "the
  tooltip shows wrong dice" that sections 24-26 settle) reads hits, statuses, casts, functors
  and damage events, and polls the game's AI for every candidate action with its scores - which
  this toolkit does not have. The roll events would replace its reading of rolls out of hits
  (about 120 lines) and add what a hit never holds: checks, initiative, each die, and a roll as
  it was before an interrupt changed it.
- The critical-miss mod builds "on a natural 1 of an attack" out of an interrupt, a marker
  status and two passives, and picks the counter-attack from a hand-made table of 132 spell
  names with ranges written in. The roll event is that condition in one handler; the game's own
  chance for each candidate would rank them.
- The re-layout of the combat log is pure interface: the tooltip's text is built by the game's
  code, so the lost terms of a damage die of 1 cannot be mended there.

What they lack, most wanted first: the chain of causes of a hit or a death through items,
explosions and statuses (who is to thank for a kill); which interrupt changed a roll and whose
it was; what a creature can do to another now (the AI's candidates joined with the game's
chance); the combat log's tooltip lines readable and the flag that cuts them fixable; a
reaction a mod can raise from a roll event without an interrupt of its own.

## 29. Whose interrupt changed a roll, and what a creature weighed

- **Whose interrupt.** At the moment a roll is changed the cast's own record of interrupts used
  names, for that roll's id, each interrupt's entity (its name and its owner), the roller and the
  other party, and what that interrupt asked - a fixed die or an amount to add. It can be read
  from Lua inside the change's event. Seen for a staged set-roll (owner, name, the fixed 1) and
  for the game's own Bardic Inspiration on a save (+3). An interrupt listed as used is not
  always the one that changed the roll: two were listed on one save and one did nothing. The
  one-frame components and the story's event come a tick or two later and do not name the roll.
- **What a creature weighed.** The game's AI keeps, for the creature whose turn it is
  calculating, every action it considered: the spell, the target, where it would cast from, the
  ranges, the scores and their parts, the cost, the damage it expects. The list is whole 10 to
  22 ticks before the cast; the chosen action is marked in the tick the cast's state appears. A
  goblin weighed a bite (191), a weapon attack and Sacred Flame (131 each) and a dash (25), and
  bit. The chance the AI starts each attack's score from is the game's own chance (section 22),
  number for number. Outside an AI calculation there is no such list; the game's own "can this
  be cast at that" check is not placed in this build.
