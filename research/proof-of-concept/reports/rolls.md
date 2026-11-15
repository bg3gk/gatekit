# Chronicle of the executor "rolls"

A work record, appended as the work goes. Copy `rolls`, branch `poc-rolls`.

## Rung 1: the pin's tests in the tree

**Asked.** Bring `scenarios/rolls_pin.lua` into `tests/rolls/` as tests of one rule each, with steps
and classes; settle the known weakness (the roll window's opening row once without `modifiers`).

**Done.**

- `tests/rolls/pin/passive.lua` - a pinned die is the natural die of a passive check or save of the
  host and of a staged creature; 19 fails the hardest difficulty (99), 20 passes it critically, 2
  passes zero, 1 fails it critically. 16 cases as named steps.
- `tests/rolls/pin/once.lua` - a once-pin decides one roll and leaves (3 ticks after the roll, at
  both speeds); six later rolls are free; a roll asked before the boost has left is pinned too.
- `tests/rolls/pin/active_window.lua` - the active roll in the window: opening row (roller,
  difficulty, modifiers that add up to the bonus), closing row (pinned die, verdict), the server's
  `game_roll` the same.
- `tests/rolls/pin/karmic.lua` - **Karmic Dice overrides a pin** (see findings); Pin switches it off
  for the scope, and then the pin holds against a high debt.
- `tests/rolls/pin/ledger.lua` - Pins() reads a pin from the creature, Clean takes it off, Verify
  names a raw `MinimumRollResult` boost as not tracked.
- Not carried over, on purpose: the refusal of an unknown kind (a check of our API, not of the game;
  `_selftest/steps` is the place, not mine); the Initiative boost text (rung 7 owns initiative).
  `scenarios/rolls_pin.lua` stays: `unit/GateKit.UnitTests/ScenarioTests.cs:729` lists it. It is
  superseded and could go with that line (not mine).

**The weakness.** A per-frame probe of the window (Lua through `exec ui`, kept in the scratchpad)
saw roller, difficulty and modifiers arrive in one frame every time, at speed 1 and 8; the fault was
not reproduced. The opening row now waits for the window's own sign that it waits for the throw: the
roller set and the state past `IntroductionAnimation` (READY_MS 4 s). Not proven as the cause; the
row can no longer be written from a reading the game has not finished.

**Proven how.** Live, the whole folder at speed 1 (run 20261115-035912-suite, 226 s) and then at
speed 8 (20261115-040310-suite, 116 s), one game session, one after another, all five passing; the reset ladder's `clean` held before every test and "after the suite: cleaned".
How the speed was set: the ladder's Clean puts the speed back to the baseline's, so a test that
does not set its speed runs at the baseline's. `GK.Speed.Set(...)` then `GK.Stage.Begin({ fresh =
true })` through `exec` before each run makes the baseline that speed; each test prints the speed it
ran at.

### Findings

- **finding (confirmed by a test): Karmic Dice overrides a pin.** A dialogue check pinned at 1
  (`MinimumRollResult;MaximumRollResult` at 1) came out natural 17, success, after four failures
  (five, three, two failures in later runs; the forced die 17, 17, 13, 18 against a target of 13),
  the boost still on the host. The earlier proof's "Karmic Dice
  does not override a pin" was made with passive Perception checks, which by bg3.wiki Karmic Dice
  does not weight - that check could not fail. Consequence: `GK.Rolls.Pin` now switches Karmic Dice
  off for the scope (`karmic = true` keeps it); the copies' profile has the option off, the installed
  game's default is on.
- **bug of ours (confirmed by `pin/karmic`, fixed): a pin replaced by one of the same n vanished.**
  Unpin and Add of the same text and cause in one call: the removal, carried out ticks later, took
  the new boost too. Pin keeps a pin of the same n; a new boost waits until a leaving one of its
  text is gone.
- finding: a save asked for by the story (`Osi.RequestPassiveRoll`) follows the natural-20 rule
  (20 passes 99), as the wiki's dialogue exception says.
- todo: a spell's saving throw pinned at 20 against a difficulty above reach - the wiki says it fails.
- todo: Karmic Dice over a pinned attack roll and a pinned save (the wiki says it weights them).
- bug (suspected, ours): `gatekit wiki rules` and `wiki page` stop near 6000 characters of a page
  ("Dice rolls" is about 100000); the natural-1 section is cut mid-word.
- bug (suspected, ours, the runner): `require` is nil in a test (the interpreter is made with
  `CoreModules.Preset_SoftSandbox`, which has no load methods), though `docs/writing-tests.md` says a
  `_` file is shared code and the runner sets a module path for it. Every test here repeats its
  small row-waiting helper by hand.
- noise: the runner prints the test's own dialogue and roll rows as "side effects of the scenario
  before, taken back" before the next test, though the test asked for them.

### Surprises listed by the runner

- `blocking dialogue (2 rows): began and ended inside the test` in `active_window` and `karmic`: real
  and meant (the check is inside a dialogue); allowed in their headers. The test waits on
  `dialogue_choice`, which does not count as naming `dialogue`.
- none in `passive`, `once`, `ledger`.

### Where time went

- The Write and Edit tools refused the worktree `poc-rolls` (a hook bound the session to another
  worktree) until the session entered it.
- `require` (above): the shared helper written first had to be inlined.
- Karmic Dice: a test written to the old claim failed on its second check - the finding.

### API review

- Improve: `GK.Rolls.Pin` decides the die only when Karmic Dice is off - done (it switches it off).
- Improve: a pin of the same n replaced itself away - done.
- Cover: `GK.Rolls.Karmic()` cannot say anything about the debt; reading it is a wish for rung 5/6.
- Cover (runner, not mine): `gatekit test --speed X`, so that both speeds are one command and not a
  baseline taken by hand; `require` of a `_` file.
- Remove: nothing.
- Faster: the passive test spends most of its time in Unpin/Pin pairs (3-4 ticks each way); a pin
  replaced by another n could skip the Unpin, but the old boost lingers beside the new one for those
  ticks, so the explicit Unpin stays.

### Answers of the coordinator to rung 1

Accepted and merged (de2163f). The wiki cut is no bug: `wiki page` / `rules` take `--chars <n>`
(default 6000). `require`, `test --speed` and a dialogue kind naming the dialogue landed on the
working branch, merged into `poc-rolls` before rung 2's commit.

## Rung 2: rolls in a dialogue

**Asked.** A check inside a real dialogue: the window, the bonuses the player may add, the result,
and what the dialogue does with it; pinned both ways.

**Done.**

- `tests/rolls/dialogue/branch.lua` - the game's dialogue `LOW_NGB_CountingHouse_UseForbiddenItem`
  goes on by its check: pinned at 20 the guard's warning line, pinned at 1 the "crime enough" line;
  window, server and dialogue agree; the line is compared with the game's localization by its
  handle, so the test holds in any language.
- `tests/rolls/dialogue/reroll.lua` - a failed check thrown again for one inspiration point: the
  window counts the throw again, the point is spent, the second die is the pin's too, and the
  server finishes one roll for the window, marked `inspired`.
- `tests/rolls/dialogue/bonus.lua` - Guidance offered in the window by the host (who is taught the
  cantrip, through the ledger), added, thrown: the d20 is the pin's 1, the d4 its own (3 in the
  first run); the server's bonus is the listed modifier and the d4.
- `tests/rolls/dialogue/_dialogue.lua` - the shared steps (the runner's `require`).
- API: `GK.Rolls.Inspiration()` / `SetInspiration(n)` (the party's points, undone by Clean);
  `game_roll.inspired` (the finished event's `ConsumedInspirationPoint`); `roll_ended.added`,
  `.inspiration`, `.rerolls`, `roll.inspiration`; `GK.Gui.RollBonuses` rows name the spell, die,
  range, giver and whether selected (they named nothing before: the offered view model has no
  `Name`/`Value` of its own); `AddRollBonus` takes the spell's name.

**Proven how.** Live, the whole of `tests/rolls` (both rungs, eight tests) with `test --speed 1`
(run 20261115-050953-suite, 268 s) and then `--speed working` (20261115-051421-suite, 161 s), one
game session, one after the other, every test passing, the ladder's `clean` holding before each
and the suite ending clean. No surprise was listed. `dialogue/bonus` runs at speed 1 in both (its
ceiling, below). The clean after `dialogue/bonus` takes about 6 s (the Guidance status and the
taught spell).

### Findings

- finding: a window thrown again is one finished roll for the server - only the last throw, with
  `ConsumedInspirationPoint`. A `once`-pin therefore covers the throw again as well.
- finding: the d20's pin (`MinimumRollResult;MaximumRollResult(SkillCheck,n)`) does not decide
  Guidance's d4.
- finding: Guidance given in the window is the cast of the cantrip: the status GUIDANCE stays on
  the roller, the next check lists it as a modifier of value 0 and adds its die by itself, and
  offers no Guidance.
- finding: the failure node sets `TemporaryHostilityAfterDialog` on the guard and the story clears
  it the next tick (seen with an Osiris `FlagSet`/`FlagCleared` listener during the study, not in a
  test); in the sandbox no hostility followed.
- finding: `Osi.GiveInspirationPoints` with a negative amount does nothing; the points are an
  action resource of an entity without a uuid.
- bug (suspected, ours, the runner): the host's interpreter (MoonSharp) mangles a pattern over
  Russian text - `s:gsub("<[^>]->", "")` and the trims turned the localized "посмотреть" into
  "постреть"; the same chunk in the game's Lua is right. The test strips the markup in the game.
- noise (suspected, ours): `GK.Gui.Resolve{ dialogue = 'last' }` called while an answer just given
  has not yet taken chose "Attack" once during the study; the tests only resolve after the roll
  window has opened.
- todo: what `TemporaryHostilityAfterDialog` does in a real level; a check that allows no bonus.
- bug (suspected, ours, `Server/Rolls.lua`), reported by the attacks executor: on a copy freshly
  restarted (`down`, `up`) a pin answered "not on after 90 ticks" in three runs in a row
  (Initiative, then Damage, then Attack, one a run) and never again in that session. Suspect: the
  first boosts after a start take far longer than the three ticks measured later, past Pin's 3 s
  limit. To look at with rung 4 (a save and a load restart the same machinery).

- **finding (the game at speed): Guidance's die is lost above speed 1.** Guidance added in the
  window is cast when the dice are thrown (its status is not on before); the roll takes its bonus
  after a span of game time, the status lands ticks later. At speed 8 the d4 was missing in every
  run (7 of 7; status 38 ticks before the finished roll), at speed 1 never (7 of 7; 57 ticks), at 4
  never (4 of 4), at 2 once in 4. Played animation or waits in the window change nothing. So
  `dialogue/bonus` sets speed 1 itself - a ceiling written in its leading comment until headers
  can declare one. Whose bug: the game's under acceleration, suspected; a player at speed 1 does
  not meet it.
- **bug of ours (seen once at speed 8, fixed): a finished roll joined to the wrong RollResult.**
  `pin/active_window` (pinned at 20) got a `game_roll` row with natural 1, `pinned = 1`,
  `inspired` - the roll of `dialogue/reroll`, the test before. `GK.Rolls.Finished` kept an
  unjoined finished roll for 30 ticks and gave the oldest to the roller's next RollResult; the
  RollResult comes the next tick, so the window is now 5 ticks. Where the unjoined entry came from
  is not known (a probe of `reroll` alone saw one finished event and one RollResult).

### Wishes for the coordinator (shared files)

- A `game_flag` family in the event log (Osiris `FlagSet` / `FlagCleared`, with the speaker and
  the dialogue instance): then `dialogue/branch` would check the flag the failure node sets, the
  one thing the dialogue does beyond its lines. Not built: `GameLog.lua` beyond the roll rows and
  the registry of families are yours.
- A way for a header to declare a speed ceiling (decided, not built): `dialogue/bonus` sets speed 1
  in its first cheat step meanwhile.
- `GK.Gui.Resolve{ dialogue = 'last' }` answers a choice that is still on screen after an answer
  was given; a guard (an answer just given, not yet taken) would belong in `Client/Popups.lua`'s
  dialogue part, which is not mine.

- **bug of ours (found and fixed): `GK.Rolls.Pins()` failed on a ledger row of kit `Rolls` that is
  not a pin.** `dialogue/bonus` first tracked its taught spell under kit `Rolls`; `pinRows` took
  every row of the kit, so `Pins()` raised an error and `Unpin` with no kind would have undone the
  spell as a pin. `pinRows` now takes only boost rows with a roll kind; the test tracks under
  `RollsTest`.
- not explained: one run at speed 1 saw four `game_roll` rows for the window of `dialogue/reroll`
  (the run that also had the `Pins()` error); not seen again. `reroll` now prints the rows if it
  happens.

### Where time went (rung 2)

- Two suite runs spoiled by my own probes: I ran `exec` chunks (`GK.Stage.Begin`, a talk, a
  passive roll) in the copy while a suite was running in it, and once left a probe's scene behind
  before a run. The verdicts of those runs are not counted.
- The lost Guidance die at speed 8: a wait on the GUIDANCE status before the throw was tried and
  is wrong - the status only comes with the throw.

### Surprises listed by the runner (rung 2)

- `dialogue` in all three dialogue tests: meant, allowed in their headers (the runner's change makes
  `dialogue_choice` name it; the allowance stays as asked).
- None else.

### API review (rung 2)

- Improve, done: `GK.Gui.RollBonuses` named nothing of an offer (the view model has no
  Name/Value); it now names the spell, die, range, giver and selection; `AddRollBonus` takes the
  spell. `roll_ended` says what was added, the points left and the throws again; `game_roll` says
  when a point was spent. `GK.Rolls.Finished` no longer joins a stale finished roll.
- Cover, done: `GK.Rolls.Inspiration` / `SetInspiration` - any test of a throw again needs points,
  and `Osi.GiveInspirationPoints` cannot be undone.
- Cover, open: a row when a bonus's status lands (it decides whether the die counts); `roll_ended`
  `total` when the bonus is a range (the server's row has it).
- Remove: nothing. Faster: the dialogue tests spend most of their time in the guard's spoken lines;
  `GK.Gui.Resolve` skips them as the continue key does.

## Before rung 3: the pin that did not arrive after a cold start

**Asked.** In a freshly started copy at speed 8 `GK.Rolls.Pin{ kind = 'Initiative', n = 100 }`
answered "not on after 90 ticks"; find the cause, fix it in `Server/Rolls.lua`, prove it from a
closed copy at both speeds.

**The cause - not the speed, not the game: a stale ledger row kept the pin from being added.**
Reproduced (cold `up`, `Begin{fresh=true}`, `Speed.Set(8)`, the pin), then taken apart with a
per-tick probe of the host's boosts and its `ServerBoostStoryRequests`, and with `Osi.AddBoosts`
wrapped to log its calls:

- the 90 ticks are 3 s of real time at any speed (a tick is longer in game time, not more frequent:
  `GK.Speed.Clock`); in the failure the boost was not late but never asked: `AddBoosts` was not
  called, the request component never appeared, and 100 game seconds later there was still nothing;
- a direct `Osi.AddBoosts` in the same cold copy at 8 - with and without `Begin{fresh}`, bare uuid,
  cause `GKPin`, the same text - arrived in 2 ticks every time;
- the same failing pin at speed 1 failed the same way; a pin of another n then held at once.
- `GK.Stage.Open("kit")` after the start listed a `Rolls` row "pin Initiative ... at 100", carried
  from the session before (my first probe had left it open; `down` does not clean). GK.Kit carries
  every kit row a previous Lua state left open into the new log (Kits.lua), and that log outlives a
  restart of the game. Pin found a pin of the same who, kind and n, **kept** it as "already on"
  (rung 1's fix: a pin of the same n is not taken off and added again) and waited for a boost a
  fresh level never had.

So the coordinator's "once one pin was made at speed 1, pins at 8 work" is a pin of another n (or a
Clean) that undid the stale row; and the combat tests' first pin failing one per run is each test's
first pin matching a row carried from an earlier run. Speed had no part.

A second face of the same, found while proving: with the fix's first form (re-add the boost of a
carried row) the pin held, but `dialogue/bonus`' Clean did not take it off and the next reset fell
to `level`: GK.Stage.Clean undoes only its own scope's rows, and a kit row of an earlier scope (a
carried one too) stays open for GK.Kit.Clear.

**Fixed (`Server/Rolls.lua`).** Pin keeps a row of the same n only when it belongs to this scope; a
pin row of an earlier scope or a carried one is undone (`roll_unpin` why "an earlier scope's") and
the pin made anew in this scope. A kept row of this scope whose boost is not on and was never asked
for by this Lua state gets its boost added again (`Rolls.Asked`). A finished roll counts as pinned
(and a once-pin leaves) only when the row's boost is on the roller.

**And a second cold-start defect, found by the same runs: `SetInspiration` on a fresh level.**
`dialogue/reroll` failed from a cold copy: "no entity holds the InspirationPoint resource". The
party entity (it has `PartyView`, `PartyComposition`, ...) gets its row of the resource with the
first point ever given (`Osi.GiveInspirationPoints(char, 1, "", "")`; an amount of 0 makes no
row). In rung 2 the copy had been given points during the study, so the row existed. Now
SetInspiration gives one point when there is no row, waits for it (observed), writes n and has Clean
write 0 back; the row stays, as the game keeps it.

**`dialogue/bonus`:** `speed_ceiling = 1` in its header; its first step no longer sets the speed.

**Proven how.** Live, from a closed copy each time (`down`, then the runner starts it):
`test tests/rolls --speed working` (run 20261115-054650-suite, 161 s, eight of eight, every `clean`
held) and then `--speed 1` (20261115-054937-suite, 253 s, eight of eight, every `clean` held; the
runner starts each from a closed copy and closes it after). The copy then carried a pile of stale kit rows from earlier sessions
(pins of creatures long deleted, Talker rows), and one stale pin of the host matching a test's
first pin (SkillCheck 20) had been planted for the first run.

### Findings (pin defect)

- **bug of ours (confirmed, fixed): a pin of the same n kept a ledger row of another scope or Lua
  state as if its boost were on.** Above.
- **bug (suspected, not mine: Kits.lua / the ladder): kit rows are carried forever.** Every start
  of the bridge carries the kit rows the previous Lua state left open, and nothing closes a row
  whose entity is gone: the rolls copy carried 14 such rows (Talker spawns and dialogues of
  creatures long deleted, pins of creatures long deleted), some first written 700 rows and several
  sessions ago. No Clean undoes them (they are of no scope) and `down` does not either. A wish: a
  carried row whose entity no longer exists is closed at the carry, and the ladder's first reset
  of a fresh game runs GK.Kit.Clear (on a fresh level its boosts and spells are not there anyway).
- finding: the `InspirationPoint` resource row is the party entity's and is made by the first
  point given; a fresh level has none.
- surprise `blocking tutorial (1 rows): began and ended inside the test` in the first dialogue test
  of a cold copy (`dialogue/bonus`): real, the game's first-time tutorial of the roll window on a
  fresh profile (seen in the cold runs at working speed, not in the cold run at speed 1); not
  meant by the test, so not allowed in a header. No other surprise in either run.

### Where time went (pin defect)

- About an hour: five cold restarts to separate the speed, the stage, the text, the uuid and the
  call itself; the wrap of `Osi.AddBoosts` showed at once that it was never called.

### Answers of the coordinator (pin defect)

Accepted and merged. The carried kit rows are fixed at the root on the working branch (a Lua
state carries nothing from a log written by another game process), merged into `poc-rolls`
(1f0beef); the tutorial joins the to-do of the fight's tutorial.

## Rung 3: advantage and disadvantage

**Asked.** How two dice are drawn and which is kept; what a pin does to each; that the rows say
which applied.

**Done.**

- `tests/rolls/advantage/two_dice.lua` - the host's passive Perception checks (16 a case) with
  neither, `Advantage(Skill,Perception)`, `Disadvantage(Skill,Perception)` and both: with advantage
  the kept die is never the lower of the two, with disadvantage never the higher; with neither and
  with both it is the higher and the lower alike (no rule picks); the row names which applied.
  The rule is checked per roll, because the row now carries both dice.
- `tests/rolls/advantage/pin.lua` - a pin holds both d20 at n: advantage pinned at 1 gives 1 and 1
  (a failure), disadvantage pinned at 20 gives 20 and 20.
- `tests/rolls/dialogue/advantage.lua` - the guard's Persuasion check in the roll window with
  advantage, then disadvantage, free dice (Karmic Dice off): the window says which applies, shows
  two dice and keeps the higher (the lower); the server keeps the same die and its other die is
  the window's other. It is in `dialogue/` because the runner's `require` reaches only a test's own
  folder: `_dialogue.lua` got `give`/`take` of a tracked boost, the same as `advantage/_adv.lua`.
- API (`Server/Rolls.lua`, `game_roll`): `advantage` is read from the roll's request
  (`RequestedRoll.Roll.Advantage/Disadvantage`) and says `both` too; `discarded` is the other d20
  (`Result.DiscardedDiceTotal`). An active roll's request is destroyed before its finished event,
  so it is now read in its destruction (`Ext.Entity.OnDestroy("RequestedRoll")`, values copied by
  the roll's uuid): active rows get `discarded`, `total`, `critical` and `modifiers`, which they
  never had (registry: `game_roll.discarded`, the texts of `advantage` and `natural`).
- Fixed on the way (`Pin`, my own fix of the night): undoing a row whose boost was never on (a row
  an earlier Lua state left) asks the game for a removal that it carries out 3-4 ticks later, and
  that removal took the new boost of the same text, added at once because nothing showed the old
  one leaving. Seen: a pin at 1 answered in force and the next twelve rolls were free. Such a
  removal now counts as leaving for 8 ticks before the new boost is added.

**Proven how.** Live, from a closed copy: the whole of `tests/rolls` (eleven tests) with `test
--speed working` (run 20261115-061239-suite): ten passing, `dialogue/bonus` failing on its known
lost d4 because it ran at 8 (below, a runner bug); then `--speed 1` (20261115-061912-suite, 355 s),
all eleven passing; every `clean` held in both. The rung's three tests pass at both speeds.

### Findings (rung 3)

- **finding: the game draws two d20 for every roll.** `Result.DiscardedDiceTotal` is set on a
  plain roll too; with advantage the higher is kept, with disadvantage the lower, with neither or
  both one of the two with no rule (as the wiki says, the result is as one die).
- **finding: the finished-roll event's `Advantage`/`Disadvantage` are false for a passive roll
  with an Advantage boost** (and `AdvantageType` of the request stays `None`); the request's
  `Roll.Advantage` says it. For an active roll the event's flags are set. The rows had never said
  advantage for a passive roll before.
- **finding: a pin decides both dice**, so with a pin advantage and disadvantage change nothing.
- **finding: an active roll's request is destroyed before its finished event** (the event's `Roll`
  entity is `[DEAD]`): this is why the active rows had no modifiers ("a roll that is gone by then").
- finding (for rung 5): from a fresh start of the copy the host's first passive Perception checks
  came the same pairs in two cold starts (9/11 11/9 5/17 9/1 9/4 17/9 18/20 1/4 ...; a third run
  continued the same sequence after the eight rolls of the test before it).
- **bug (suspected, not mine: the runner, `ScenarioRunner.cs:359`): `speed_ceiling` holds only for
  the first test of a group.** `speedBefore ??= await HoldSpeedAsync(...)` calls it only while
  `speedBefore` is null; `dialogue/bonus` ran at 8 (its line "speed: 8", no ceiling line) when a
  test came before it in the group, and at 1 when it was the first (the night's earlier runs).
- todo: an attack roll with advantage - no data the bridge reads has an attack's natural dice.

### Surprises listed by the runner (rung 3)

- `dialogue` in `dialogue/advantage`: meant (the check is a dialogue's), allowed in its header.
- `blocking tutorial` in the first dialogue test of a cold copy (now `dialogue/advantage`, at
  speed 1 this time too): the game's own, not meant, not allowed (the coordinator's to-do).

### API review (rung 3)

- Improve, done: `game_roll.advantage` was empty for every passive roll with advantage; it is read
  from the request now, with `both`. Active rows had no modifiers, total from the game or critical:
  read in the request's destruction.
- Cover, done: `game_roll.discarded`, the other die - without it the rule could only be shown by
  chance over many rolls.
- Cover, open: a verb to give advantage for a scope (the tests track a boost by hand, twice, as
  the runner's `require` reaches no other folder) - not built: two tests do not make a verb.
- Remove: nothing. Faster: nothing slow in this rung (the passive tests take 5-8 s).

### Where time went (rung 3)

- The runner's `game.wait` takes an expression, not statements: one run lost.
- `require("../dialogue/_dialogue")` is not found: the window test moved into `dialogue/`.

## The red unit test

`RollsTests.Once_takes_the_pin_off_after_the_holders_next_finished_roll_of_the_kind_only` failed
with "attempt to index a nil value": not a missing fake. Rung 2 made `GK.Rolls.Finished` drop a
finished roll after 5 ticks (`FINISHED_TICKS`, so that a stale one is never joined to a later
roll); the test read it 7 ticks after the rolls, both were dropped, and `.natural` indexed nil.
Rolls.lua is right; the test now takes the rolls within the window and checks that an unjoined one
is dropped after it (004d129). All of `dotnet test` green.

## Rung 4: a save, then a load

**Asked.** Whether the next roll after a load repeats: is the outcome fixed by something kept in
the save or drawn fresh. What players say, then a test.

**Done.**

- What players say (study, section 4): most reload to have a failed check thrown again; a Steam
  thread reports the same results three reloads in a row; bg3.wiki ties Bestowed Bounty's items to
  the save.
- `tests/rolls/save/load.lua` - a save (`game.checkpoint()`), eight passive checks, a load
  (`game.reset()`), eight checks, a second load, eight checks: after each load the generic stream
  counts from 0 with the seed base+1, the base seed is not the save's, two loads' base seeds are
  apart by the seconds between them, and the dice differ each time. `reset = "level"`; the test
  ends with a fresh `Begin` (the load ends the scope; the world is the save's, made on the clean
  scene the test began on). `surprises = { "loading" }`: meant.
- API: `GK.Rolls.Streams()` (observation; VerbClasses, lua-types, lua-api.md section 17): the roll
  manager's base seed and every stream's type, seed, count, frame and debts, and the component's
  copy as `saved`. Rung 5 writes these back.
- The duplicated `give`/`take` of `dialogue/_dialogue.lua` removed: `dialogue/advantage` requires
  `advantage/_adv`, as `save/load` does.

**Proven how.** Live, the whole of `tests/rolls` (twelve tests) on the committed tree (cc7895b),
each run from a closed copy, one after the other: `--speed working` (run 20261115-070214-suite,
272 s) and `--speed 1` (20261115-070650-suite, 391 s), twelve of twelve in each, every reset held
(`clean` ten times, `level` once before `save/load`). The same before the tick-limit fix:
20261115-064722-suite (working) and 20261115-065248-suite (1), twelve of twelve each.
`dialogue/bonus`, fourth in its group, ran at 1 by its ceiling ("speed: 1 (the test's ceiling: 1);
the game ran at 8.0") and passed: the runner's fix holds in any order.

### The pin that failed once in `coord` at the working speed

The coordinator's run 20261115-063140-suite: `pin/passive`, "staged Perception pinned at 2", the
answer "the boost was asked for and is not on ... after **1** ticks" with `waitedMs = 6964`. The
server ticked once in seven seconds of real time: the game stalled (the step before took 2.8 s
instead of about 1.1), and the answer's limit was 3000 ms of real time, so the first tick after the
stall was already the final look. Not the leaving guard of rung 3 and not the n.

Reproduced in `rolls`: a tick handler that holds the server for 5 s on the tick after `Pin` gives
the same answer ("not on ... after 1 ticks", `waitedMs` 5028). **Fixed (`Server/Rolls.lua`):** every
observed answer of `GK.Rolls` (Pin, Unpin, SetKarmic, SetInspiration) waits 90 server ticks - about
3 s of a running game - and 30 s of real time only as a guard against a game that stopped; the same
5-s stall then gives the pin in force after 2 ticks (twice). Unit test
`A_pins_limit_is_counted_in_server_ticks_so_a_stall_of_the_game_is_no_failure`. What stalled the
coordinator's game is not known (suspected: the machine's load with several copies running).
Wish (the bridge, not mine): `GK.Observe` could count its `within` in ticks for every verb - the
same stall would fail any observed answer with a limit of real time.

### Findings (rung 4)

- **finding: a load draws the dice fresh.** It sets the roll manager's base seed to a clock in
  seconds (the Unix time of the load) and makes the streams anew: the generic stream's seed is the
  base and one, its count 0. A save keeps nothing of the next roll; reloading throws a failed check
  again, as most players say. Two loads in one second would draw alike (not shown).
- **finding: a cold start of a level always draws the same dice.** Without a save the generic
  stream's seed is 1 (the base seed is the clock all the same): the host's first checks were
  9/11 11/9 5/17 9/1 9/4 17/9 18/20 1/4 in a third cold start, as in rung 3's two. For rung 5.
- finding: every d20 roll draws two from its stream (`NumRolls` + 2): the second die of section 3.
- finding: a dialogue check draws from a `Dialog` stream of the roller (`PlayerUuid` the host),
  made at the first dialogue check with the seed base+2 (one probe).
- todo: Karmic Dice's debt is a stream's (`FailDebt`, `SuccessDebt`), so a load would forgive it;
  not tested with Karmic Dice on.
- wish (`gatekit save`): read a save's components (LSLib reads `.lsv`), to say whether the save
  holds the streams at all.
- wish (the runner): a test that calls `game.reset()` ends its scope; the runner could take the
  new baseline after it as the `level` rung does, instead of the test's own `Begin{ fresh = true }`.
  Without that step the suite ended "not clean: no baseline".

### Surprises listed by the runner (rung 4)

- `blocking loading (2 rows)` in `save/load`: real and meant (two loads), allowed in its header.
- `blocking tutorial` in `dialogue/advantage`, the first dialogue test of a cold copy: real, the
  game's first-time tutorial, not allowed (the coordinator's to-do).

### API review (rung 4)

- Cover, done: `GK.Rolls.Streams()` - the dice's source was readable only through raw component
  reads.
- Cover, open: writing the streams (rung 5); a row when a load re-seeds them (`game_state` rows
  already mark the load).
- Remove: nothing. Faster: `save/load` spends 25 of its 40 s in two loads; one load would show the
  re-seed but not that it follows the clock.

### Where time went (rung 4)

- Little: the first probe (four checks, a checkpoint, a load) gave the answer.

## Rung 5: the roll streams and their seed (cut short by the change of course)

**Asked.** Full repeatability through the streams; why the earlier write-back diverged after twenty
rolls. Then cut by the coordinator: rung 5 only as far as it is the instruments' work, rung 6 the
body of the night, rung 7 dropped.

**Found by probes (exec in `rolls`, cold start of the sandbox, Karmic Dice off), not yet a test:**

- **The dice of a stream are a function of its seed and count.** The Generic stream of a cold
  start (seed 1, count 0): 40 checks; seed and count written back in the component - the same 40
  pairs again, exactly. Count written to 16: the 9th roll's pair onwards. Seed 2, then the
  clock's seed, then 1 again: two new sequences and the first one again. An odd count gives pairs
  not in the even sequence: each roll's pair is drawn from the count it starts at.
- **The game draws from the component (`ServerRollStreams`); a write to the roll manager's stream
  is not honoured** (the manager's count went on from the component's). The earlier write-back
  that "diverged after twenty rolls" is not reproduced: 40 of 40 held. Suspects, from what was
  seen: every roller's passive checks draw from the one Generic stream (a staged creature's two
  checks after a write-back took the host's first two pairs), so any other roll in between shifts
  the sequence; and a dialogue check draws 2 from Generic besides its own 2 from the Dialog
  stream (what the Generic pair is for, unknown).
- **A dialogue check's dice come from the Dialog stream**, rewound the same way: its count written
  back to 2, then 0, gave the window 8/14 and 6/8 again, the pairs of the second and first check.
  The Dialog stream's `PlayerUuid` is not the host character's uuid (another id of the player).
- The runs of rung 3 agree: `advantage/two_dice` at speed 1 from a cold start drew the same pairs
  as a cold start's 9th-16th and 25th-40th rolls, advantage only choosing which of each pair is
  kept.
- API: `GK.Rolls.SetStreams(list)` (cheat; VerbClasses, lua-types, lua-api.md): writes seed and
  count of named streams; an earlier `Streams()` reading rewinds them all; Clean writes the old
  values back (shown: a rewind of Generic and Dialog repeated six checks, Clean put the count back
  to 24). Committed 7399a61; no test of the tree yet.

**Decide every roll at once: not reached.** Reached is *repeat*: a test takes `Streams()` and later
`SetStreams(it)`, and every passive check and dialogue check comes again with the same dice, as
long as the same rolls are asked in the same order (another roller in between shifts the shared
Generic stream). *Choosing* a die is not reached: nothing maps a wanted die to a seed and count
except drawing them; the function the game draws with (rung 6) would. Attack, damage and spell
rolls (the Spell and Damage streams of a fight) are not tried. (See also the freeze below: the
count held by `GK.Memory.Freeze` makes every roll draw one pair.)

## Rung 6: the descent - every instrument tried once, on "where the game draws a number"

The copy `rolls`, sandbox, three other games running beside it. Addresses are of `bg3_dx11.exe`.

**1. The memory instrument (`GK.Memory`, Server/Memory.lua on the fork's `Ext.Debug`).** Built
and working - "who writes this field" is not done by hand: `GK.Memory.Watch` then `Hits` name the
instruction.
- `GK.Memory.Available()` -> true. `GK.Memory.Field(entity, "esv::roll::stream::StreamsComponent",
  "BaseSeed")` -> `{ Address, Offset = 24, Type = "uint64", Size = 8 }`, first call. A field inside
  an array element is not reachable by `Field` (only the component's own fields): `Field(...,
  "Streams")` gives the array, `GK.Memory.Read(array, "uint64")` its buffer, and the Generic
  stream's count is buffer + 0x70 (Script Extender's `Stream` layout, `State` at +0x58,
  `NumRolls` at +0x18 of it); read there: 24, equal to the Lua value.
- `GK.Memory.Watch(that, 8)` -> slot 1; one check asked; `GK.Memory.Hits()` -> one hit at
  +0x105DBBD, its return chain into `SystemUpdate<esv::roll::stream::SaveSystem>` (+0x23993CC).
  That is a copy, not the draw: the component is the save's copy of the roll manager.
- The live manager: `Ext.Debug.GetSystemAddress("esv::roll::stream::SaveSystem")` (a fork call
  `GK.Memory` does not wrap, not in the contract) gives the system; its +0x18 holds the manager
  (base seed at manager+0x20 read 1794716171, the Lua value); manager+0x8 the streams. Watched
  there over one check: two hits at **+0x105AAE3** (26 -> 27, 27 -> 28) - the draw, once per die -
  and one hit every frame at +0x105DBBD from `SystemUpdate<esv::roll::stream::UpdateSystem>`
  (+0x238ABC3) writing the same value: the component is copied into the manager every frame.
- `GK.Memory.Freeze(component count, "uint64", 0)` holds it: three checks asked a second apart all
  came 9/11, the pair at count 0.
- Took about 20 minutes, most of it finding the manager. Nothing misbehaved with other games
  running. Wishes: `GK.Memory.Field` reaching into an array element (`Streams[1].State.NumRolls`);
  `GK.Memory.System(name)` wrapping `GetSystemAddress` in the contract.

**2. `gatekit decomp at` / `decomp code`.** Every address answered at the first call, about 1 s
each, as "inside function at ...": +0x105AAE3 in +0x105AAB0 (unnamed `FUN_`), +0x105DBBD in
+0x105DB40, the callers +0x105C9B0, +0x105CCA0, +0x105B800, +0x1075BA0, +0x11888B0, the systems'
`SystemUpdate<esv::roll::stream::SaveSystem>` (+0x2399390) and `<...::UpdateSystem>` (+0x238A9D0)
by name, `ecs::EntityWorld::Update` (+0x40033C0) from the symbols. `decomp code` read them (kept to
myself). What the code does, in my words:
- +0x105AAB0 **draws one number**: it puts a generator at the stream's count (a call to
  +0x105D570 with the count), adds one to the count, and returns min + (the generator's value
  modulo the range); when the roll manager's custom expected-value flag is set it draws a rounded
  value around an expected one instead. The generator object holds a pointer to the count at
  +0x1390 and that flag at +0x1398. So a die is a function of seed and count - as rung 5 showed
  from outside.
- +0x105DB40 copies one roll stream field by field; its widths and offsets agree with Script
  Extender's `Stream` and `PRNGState` (+0x30 and +0x4C FixedStrings, +0x38 a Guid, +0x48 a byte,
  +0x50 the type, +0x58 Frame, +0x60/+0x64 the debts, +0x68 Seed, +0x70 NumRolls): none found
  wrong.
- In +0x342D9D0 (the SaveSystem's work): when the manager's +0x2A flag is set (`HasChanges`) its
  streams are copied into the component, its +0x18 into the component's +0x10, and the flag
  cleared; when the component is first made, its +0x18 (`BaseSeed`) is the C runtime's time in
  seconds - **what writes the base seed**.
- Missing: a way to name a function. `decomp at` answers `FUN_14105aab0` and nothing in `gatekit`
  writes a function's name (`names set` names fields of a layout; `decomp refresh` re-decompiles
  what was renamed elsewhere). The names I would give: +0x105AAB0 the stream's draw of one number
  in a range, +0x105DB40 the stream's copy, +0x105D570 the generator's positioning at a count -
  written here instead.

**3. The tracer** (`Ext.Entity.SetupTracing{ TrackECB, TrackImmediateWorldCache,
TrackReplication, TrackModifications, ExcludeModificationComponents }`, `EnableTracing`,
`ClearTrace`, `GetTrace` through `exec server`). Works; its options are documented nowhere in the
repository (read in the fork's `Lua/Libs/Entity.inl`). 3 s idle as the baseline, 3 s around one
passive check: the roll created `eoc::RequestedRollComponent` (created, modified, replicated),
`esv::roll::RollFinishedEventOneFrameComponent`, `esv::stats::SkillCheckEventOneFrameComponent`,
an analytics `EventRollComponent`, a skill-check notification and the tutorial's notification
request. `esv::roll::stream::StreamsComponent` is modified in the baseline too (every frame), so
a subtraction by name hides the roll's own change of the streams - the memory watch is what showed
it. The order of the changes: not read (this probe summed by component; the trace has frame and
flush stamps per change only in the fork's unbuilt tracer branch).

**4. Names.** `gatekit names set` written, source `inferred`, with the instrument readings as
evidence: `esv::roll::stream::StreamsComponent` +0x18 `BaseSeed` and +0x0 `Streams` (in
`research/coverage/layouts/extender/esv.roll.stream.StreamsComponent.json`). The `Stream` struct's
fields could not be named: the record has no nested struct `Stream` ("has no nested struct"). The
roll manager is a system's object, not a component: no layout to name in. `gatekit names extender`
lists nothing for the roll streams (every field there is already at rung `extender`); checked
against the code: `Streams`, `BaseSeed`, the `Stream` layout, the manager's `HasChanges` (+0x2A)
and `BaseSeed` (+0x20) hold.

**5. The questions.**
- Where the game draws a number: **+0x105AAB0** (the count written at +0x105AAE3), once per die,
  two per d20 roll. Proven by the watch on the manager's count.
- Where the pin's minimum and maximum apply: **not yet** - the callers of the draw (+0x105C9B0
  from +0x105CCA0, from +0x105B800 ...) are where a clamp would be; next: the same watch with a pin
  on and `Hits` compared, then those callers read.
- Where the second d20 is drawn and discarded: **not yet** - both increments came through the same
  caller chain; which caller keeps one is to read.
- What a load writes into the base seed: the time in seconds when the streams component is made
  (+0x342D9D0); the stream's seed being the base and one is not located yet.
- Why writing the component works and writing the manager does not: the UpdateSystem copies the
  component into the manager every frame (+0x238ABC3 hits each frame), and the SaveSystem copies
  the manager back when it changed.

### Rung 6, step 1: the pin's clamp and the kept die

- **The watch with a pin on.** The host pinned at SkillCheck 20, the roll manager's count watched,
  one passive check: two hits at +0x105AAE3 (0 -> 1, 1 -> 2), the same return chain as without a
  pin, and the check came 20/20 where the stream at count 0 gives 9/11. **The pin does not skip
  the draw: both dice are drawn and then clamped.**
- **Where it clamps** (read in the callers): +0x105CCA0 rolls the formula with its dice doubled
  (the second d20), through +0x105C9B0 (a 64-bit Mersenne Twister seeded with the stream's seed,
  each die drawn by +0x105AAB0 at the stream's count, the success and failure debts moved by the
  outcome - the Karmic debt lives here); then puts every die through +0x105AD10, which reads the
  roller's `BoostsContainer`, and for each `MaximumRollResultBoost` of the roll type lowers the
  die to its `Result` (and raises it for each `MinimumRollResultBoost`). Then, when advantage and
  disadvantage differ, +0x105CCA0 keeps the higher or the lower of each pair; with both or
  neither, one of the pair. So **which d20 is kept** is decided in +0x105CCA0, after the clamp.
- **Meeting the attacks executor:** +0x105AD10 calls `ecs::GetComponent<eoc::MaximumRollResultBoostComponent>`,
  the same template as its getters at +0xAE6C40, +0xB2FE60, +0xCA0D60 (`decomp at` names all three
  so); which of the three it calls is not read. +0x105CCA0 is likely the Toolkit's
  `RollManager::InternalRollDiceWithBoosts` (both roll dice and apply boosts) - not paired by an
  instrument, so not named so.
- **Crash, twice: a read watch on the pin's boost.** `GK.Memory.Watch(<MinimumRollResultBoost.Result>,
  1, true)` (the address from `GK.Memory.Field(boostEntity, "eoc::MinimumRollResultBoostComponent",
  "Result")`, which read 20 correctly) - armed alone, no hit and no harm; then a passive check
  asked, and the game closed (no answer in 20 s, nothing in Script Extender's log). The first time
  the same chunk armed it and the check came in the next command. Restarted with `down`, `up` each
  time. Bug (suspected, the fork's watch): a read watch hit by the roll closes the game. Write
  watches did not.
- **Names entered** (`gatekit decomp name`, merged from the working branch; ledger
  `research/coverage/functions.jsonl`, each with its evidence and `--does`):
  `+0x105DB40 esv::roll::stream::CopyStream`, `+0x342D9D0 esv::roll::stream::SaveSystem::SaveStreams`,
  `+0x105C9B0 esv::roll::stream::RollDice`, `+0x105CCA0 esv::roll::stream::RollDiceWithBoosts`,
  `+0x105AD10 esv::roll::stream::ApplyRollResultBoosts` (the coordinator entered
  `+0x105AAB0 esv::roll::stream::DrawNumber`).
- Bug (suspected, the new command): `decomp at` shows the name for an address inside a named
  function (`+0x105AD20`, `+0x105AAE3`) but not for the function's own start (`+0x105AD10`).

### Rung 6, step 2: where a stream gets its seed (base seed + id)

Copy `rolls` started fresh (`up`, 29 s), `checkpoint` made; other games running beside it.

- **Watch across a load, first try: the old addresses are freed.** `GK.Memory.Watch` on the
  manager's Generic `Seed` (buffer + 0x68), its count (+0x70), the manager's base (+0x20) and the
  component's `BaseSeed` (`Field(...,"BaseSeed")`), then `reset`. Expected: the write of the new
  seed. Got: the component and its stream buffer are allocated anew by the load (the component's
  address changed); the old addresses were overwritten by unrelated code (+0xACA871, +0x4D7D8F1,
  +0xB60C19 ... with pointer-like values) - the known "storage moved" defect, here on a load.
- **Ring overflow.** The manager's fields are rewritten every frame by the copy, also during the
  load (the ring of 512 hits filled with frame-copy hits; all stamped with one `Frame`, because
  after the load the new Lua state does not run `GK.Memory`'s tick hook - the watch stays armed
  in the fork, `Frame` stops and new threads are not re-armed; Stage.Clean of the new state
  does not know the watch). Filtered on `Before ~= After`, the second try (manager base +
  Generic seed only, the buffer stayed in place this time) showed both new values arriving by
  the per-frame copies from the component (+0x238ABCF base, +0x105DBB5 seed, from
  `SystemUpdate<UpdateSystem>`): the manager is not where they are made.
- **Static detour.** The Toolkit's `EoCServer.dll` has `esv::roll::stream::RollManager::CreateStream`
  and `GetGenericStream` by name; the game's `decomp code` marked +0x309FBB0 as a weak match
  ("~ CreateStream [pseudo-code 0.20]").
- **The watch that settled it: a new stream made live.** `GK.Memory.Watch(manager + 0x18, 8)`
  (Script Extender's `LastFrame`) and `(manager + 0x2A, 1)` (`HasChanges`), then a dialogue
  check (`GK.Kit.Talker{ dialog = 'roll' }`, `GK.Cheat.Talk`, `GK.Answer(1)`), the first of
  this load: one change **1 -> 2 at +0x309FD9E** (return chain +0x309F5E9, +0x3295F63,
  +0x32981CF, ...), and `HasChanges` cleared 1 -> 0 at +0x342DE05 (SaveStreams). Read after:
  Generic seed base+1, id 1; Dialog seed **base+2**, id 2. The 0 -> 1 of `HasChanges` is hidden
  by the `Before` defect (a slot's first hit shows `Before = After`).
- **What the code does** (read): +0x309FBB0 makes a stream of a type: the manager's last stream
  id plus one is stored back and becomes the stream's +0x58, the base seed plus that id its
  seed (+0x68), its count zero; sets `HasChanges`. +0x309F570 looks for the Dialog stream (type
  1) of a player id (+0x4C) and makes it when there is none. **So a stream's seed is the base
  seed plus its id; Generic is base + 1 because it is the first stream made after a load.**
  The load's reset of the id and the streams: hypothesis, not watched - the new component
  (empty streams, its `LastFrame` 0, `BaseSeed` the time, made in SaveStreams) is copied into
  the manager by the UpdateSystem (seen: base and seed arrive by those copies), and the next
  Generic draw makes Generic as stream 1.
- **Names** (`decomp name`, each first call): `+0x309FBB0 esv::roll::stream::RollManager::CreateStream`,
  `+0x309F570 esv::roll::stream::RollManager::GetDialogStream`, `+0x105D8D0
  esv::roll::stream::CopyStreams` (the frame copy: count 1 -> 0 at +0x105D919, 0 -> 1 at
  +0x105D967). Fields (`decomp field`, new, first call): `Stream+0x68 Seed`, `+0x58 Id`, `+0x50
  StreamType`; `RollManager+0x8 Streams`, `+0x18 LastStreamId`, `+0x20 BaseSeed`, `+0x2A
  HasChanges`. `decomp vars` on CreateStream (`param_1=manager`, `local_60=id`, `local_50=seed`,
  `--struct manager=...RollManager`) printed at once, the struct's fields listed above the code;
  the body's `*(longlong *)(manager + 0x18)` is not rewritten to the field's name.
- Seen on the way (read only, not named: no instrument yet): +0x105B080 collects the roller's
  `RerollBoost`s of the roll type (`RollBelow`, `AlwaysReroll`); +0x105B2C0 rerolls each die at
  or below `RollBelow` with `DrawNumber` and keeps the new die when `AlwaysReroll` or higher.
- Tools: `GK.Memory.System` (new) worked at the first call. `exec` with
  `Ext.Debug.GetSystemAddress(...)` used as a number failed once (it returns a table; `.Address`).
- Hot names tried on the draw and the stream copy: `decomp vars +0x105DB40 param_1=to param_2=from
  --struct to=...Stream --struct from=...Stream` -> `to->Seed /*+0x68*/ = ...(from + 0x68)`,
  `to->NumRolls`, `to->Id`, `to->StreamType`, `to->PlayerUuid` in the code - true against the
  reading; `from` (typed `FixedString *`) keeps raw offsets, as documented. The draw's first
  argument is not a stream but the generator: `decomp field esv::roll::stream::Generator 0x1390
  NumRollsOfStream` (the pointer the add-one writes through, hit at +0x105AAE3), `decomp vars
  +0x105AAB0 param_1=generator param_2=min param_3=max` -> `*generator->NumRollsOfStream /*+0x1390*/
  = ... + 1` and `return min + uVar2 % ((max - min) + 1)`: reads true. All first call.

### Rung 6, step 3: the kept die into the request, and the verdict

- **The draw's return chain** (watch on the manager's Generic count, `Osi.RequestPassiveRoll` of
  the host, DC 10): +0x105AAE3 <- +0x105CB3D (RollDice) <- +0x105CDA1 (RollDiceWithBoosts) <-
  +0x105B9ED (in +0x105B800) <- +0x1075CCE (in +0x1075BA0) <- +0x1188BBC (in +0x11888B0). The
  chain stops at six entries, so it does not reach the system; `symbols callers` goes on:
  +0x11888B0 <- +0x1189700 <- four callers, one the expression resolver +0x118C930 (weakly
  paired with `eoc::StatsExpressionObjectVariable::Resolve`): the roll is the evaluation of the
  request's stats expression. Wish: a longer return chain.
- **The watch on the finished roll, armed in time.** The request appears a tick after the ask and
  is rolled a tick later, so a `Tick` handler armed the watch on the first tick a new
  `RequestedRoll` existed (`Field` of `Result`, `Result+4`, `NaturalRoll`, `Finished`). Hits:
  `Result.Total` 18 at **+0xE75F73** and `Result.NaturalRoll` 15 at **+0xE75F7F** (from
  +0xF1889D <- +0x32961CE), the request's `NaturalRoll` 15 at **+0x3296664**, all from
  +0x32981CF <- +0x32937EF <- `SystemUpdate<esv::RollSystem>` (+0x2390BAC). Then the request
  moved and later hits were of other memory (+0x20B08EE ...).
- **The verdict.** A second run watched +0x4C (Script Extender's `SpellCheck`): 1 written at
  **+0x3296682** on a pass (natural 13, DC 10). A third read the request each tick on a fail
  (DC 99): `SpellCheck` 0, natural 9, total 12. Read in +0x32955C0: the rolled record is copied
  into the request's `Roll` and `Result`; success is a natural 20, failure a natural 1, else
  difficulty <= total; then the natural die (+0x42), the discarded die (+0x43, only when
  advantage and disadvantage differ, else 0), the verdict (+0x4C) are written. **So the
  comparison with the difficulty and the write of the outcome are in +0x32955C0; +0x4C is the
  verdict, not a "spell check".**
- **Crash.** The third run, first form: the same tick handler arming three write watches
  (`SpellCheck`, `NaturalRoll`, `Result` 4 bytes) on a check against DC 99 - the game closed
  (`exec` "no answer in 20 s", then "no game is running"). Same arming as the first run, which
  did not crash; the difference: the hardest difficulty, and a fresh `GK.Memory.ClearWatch`
  just before. Not reproduced (the third run read without watches). `down`, `up` (104 s).
- **`Moved` (new) is set when the hits are read, not when each was made**: the two hits
  written before the request moved (+0x3296664, +0x3296682) came with `Moved = true` too, so it
  does not tell the good hits from the bad; `Moved()` named both slots. Defect, suspected.
- **Names** (each first call): `+0x32955C0 esv::RollSystem::RollDice` (also the decompiler's
  weak pairing), `+0xE75F60 eoc::CopyStatsRollResult`, `+0xF18850 eoc::CopyRollAndResult`;
  `names set eoc::RequestedRollComponent 76 Success` (was Script Extender's `SpellCheck`).
  Not named: +0x105B800, +0x1075BA0, +0x11888B0 - on the path, by the chain, but not read far
  enough to say what each does.

### Rung 6, step 4: the tracer with its options

- `SetupTracing{ TrackECB, TrackImmediateWorldCache, TrackReplication, TrackModifications = true,
  ExcludeModificationComponents = { 'ServerRollStreams' } }`, one passive check (DC 10), 2 s:
  first call. The trace's `Frame` and `Flush` and each component's `Frame`/`Flush` are readable
  in the running build: the request at frame 4418 / flush 4419, the tutorial's roll request the
  same, `RollFinishedEventOneFrameComponent` 4431 / 4432, the analytics roll 4432 / 4433 - **the
  order reads by frame and flush**, not inside one flush. A component's `Flags`, an entity log's
  `Entity` and `Flags`: "Property does not exist" (the fork's source declares them as
  `P_BITMASK`/`P`; the build is older). **The entity resolves through the map's key** (an Entity
  object, `[DEAD]` once the request was destroyed). The how-to is in the study, section 7.

### Rung 6, step 5: a test of the tree, and Script Extender's declarations

- `tests/rolls/streams/rewind.lua`: six checks, `SetStreams` back to the reading before, the same
  six pairs, six more without the rewind differ. PASS at `--speed 1` (6.4 s), `--speed working`
  (5.8 s), `--speed 1` again (6.7 s), one session, cleaned after each.
- `names extender --ns esv::roll`, `--ns eoc::RequestedRoll`: nothing listed. Against the code:
  `RequestedRollComponent.SpellCheck` (+0x4C) is the verdict - wrong name; `PRNGState.Frame`
  is the stream's id and `LastFrame` (manager +0x18, component +0x10) the last id - wrong
  names; the rest touched holds (study, section 6). Named by `names set`: request +0x4C
  `Success`, `StreamsComponent` +0x10 `LastStreamId` (the layout's old names stay as legacy).
