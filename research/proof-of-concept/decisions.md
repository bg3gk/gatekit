# Decisions of the proof of concept

The coordinator's ledger: every decision of the run with its reason, granted or refused, in the
order it was made. A row is data, not prose to be kept current.

## Before the executors start

1. **The working branch** is the coordinator's own branch, cut from `coverage-night`. The
   executors' branches `poc-rolls`, `poc-attacks`, `poc-aiming` are cut from it, each in a
   worktree of its own, and merged back into it as rungs close. Reason: the start's own terms;
   nothing reaches `coverage-night` or `main`, nothing is pushed.
2. **Names true in the code.** None of the renaming has landed; each line of the brief's table
   was checked against `gatekit --help` and the tree, and every executor is given the right-hand
   column: the header is `scenario{...}` (the runner knows no `test{...}`); the fixtures are
   `GK.Kit`; the reset rungs are `clean`, `level`, `process`; the projects are
   `src/GateKit.Scenarios` and `src/GateKit.Stand`; the commands are `gatekit data ...`,
   `gatekit decomp run`, `gatekit sweep run`. New tests go under `tests/` (the runner reads it
   with `gatekit test tests`), written with `scenario{...}`, `step` and `need`. Existing tests
   under `scenarios/` are not renamed. Reason: "nobody renames existing code during the proof
   of concept"; an alias `test{...}` in the runner was considered and refused for the night,
   because the renaming is one piece of work with an owner of its own.
3. **An executor returns at every closed rung.** A subagent cannot be written to while it
   works, so the natural pause is made the protocol: an executor commits, reports and stops when
   a rung closes or blocks; the coordinator merges, answers and sends it on. Each keeps a
   chronicle on its own branch, `research/proof-of-concept/reports/<name>.md`, so that nothing
   said in a report exists only in a message.
4. **What an executor may not run in the shared home:** `se install` (it rewrites the default
   build of every new copy - an open item), `decomp run`, `decomp refresh`, `data build`,
   `symbols build`, and any build of the fork. Reason: three executors share one home; each of
   these rewrites what the other two read. A wish for the fork is written down, not built.
5. **A window of one's own, by the owner's word.** If the deck gets in the way - the memory
   instrument complains, the frame cap of a background tile disturbs a measurement - an
   executor may restart its copy (`down`, then `up`) so that it runs in a window wholly its
   own, outside the deck (`gatekit window release <copy>` if the deck takes it again), still
   behind other windows, and says so in its report. Granted by the owner in person before the
   executors started.
6. **Preflight.** `rolls` and `aim` passed the self-tests and the sandbox tests. In `attacks`
   `event_log` failed once ("the fight stops waiting at combat_ended": the fight was still
   waiting 235 ms after the row was expected) while three copies ran their tests at once, and
   passed when run again alone; the copy was handed over, the failure is kept as a suspected
   defect of that test's wait under load. The first `gatekit test tests` in each copy, started
   in the three copies at the same moment, answered "the game is already running, and no
   scenario asked for is declared for that level" - the level was not read; the same command
   passed minutes later. Kept as a finding against the runner.
7. **The study mods** already in the home may be read with `gatekit mod usage`; a mod that
   would have to be downloaded is written down as a wish with its address. Reason: no download
   without the owner.

## Rolls, rung 1 closed (the pin's tests in the tree)

8. **Accepted and merged** (7ab2b1c, merge de2163f): five tests under `tests/rolls/pin`, proven
   live at speed 1 and 8 in one session by the executor; the build and the unit tests green
   after the merge. The live run is the executor's own, on its branch, whose tree differed from
   the merge only by the decisions ledger.
9. **Karmic Dice overrides a pin - granted as a finding, against the brief.** `Pin` switching it
   off for the scope stands; `docs/writing-tests.md` corrected. Reason: the earlier proof used
   passive rolls, which Karmic Dice does not weight, so it could not have failed.
10. **`require` in a test - granted.** The interpreter gets its load methods. Reason: the
    documents already call a `_` file shared code; every test of every subject gains.
11. **`gatekit test --speed` - granted and built.** Reason: "both speeds" is part of done for
    every rung, and the ladder's clean puts the speed back, so each executor had invented a
    workaround of its own (a baseline by hand; a global read from the game).
12. **`scenarios/rolls_pin.lua` stays - removal refused for the night.** It is superseded by
    `tests/rolls/pin`, and `unit/GateKit.UnitTests/ScenarioTests.cs` names it. Reason: nothing
    existing is removed or renamed during the proof of concept; left for the owner.
13. **`wiki rules` and `wiki page` cut a page near 6000 characters** - recorded as a suspected
    bug of ours; not fixed tonight (no executor is blocked: the rest is read on the web).
    Checked afterwards: it is the option `--chars`, whose default is 6000 - no bug; the
    executors are told. What remains is a wish: the cut says that it cut and names the option.
14. **An executor may take the working branch into its own** (`git merge` of the working branch,
    with its tree committed) when the coordinator says something has landed. Reason: shared
    changes reach the executors no other way.

## Aiming, rung 1 closed (a clear aim, told truthfully)

15. **Accepted and merged** (27ea299, merge 7b105ce): three tests under `tests/aiming`, proven
    live at speed 1 and 8 in one session, then after `kits_and_attention` and `aim_and_observe`.
16. **The change of what `Confirm`, `CancelAim`, `Aim`, `Aiming` answer - granted** and announced
    to the attacks executor with the old and the new answer side by side. Reason: `Confirm`
    answered that targeting closed, not that anything happened; the new answer is what was seen.
17. **The known instability of `aim_and_observe` after `kits_and_attention`** does not reproduce;
    the executor traces the recorded failures to a dirty world taken as a baseline under
    `--attach` before the clean carried strays. Accepted as explained, not as proven live.
18. **Strays more than one Lua state deep** (a creature spawned two bridge reloads back enters
    the baseline; Verify stays clean) - a suspected bug of the shared stage; the coordinator's.
19. **Verify reads the server only** (the pointer, the targeting, a held aim are the client's) -
    an open item with fork wish W-0126; nothing is built in the fork tonight.
20. **`kits_and_attention` allows its seven blocking states in its header** - granted: each is
    what a kit of that test means to open.
21. **`GK.TestSpeed` in the aiming tests is replaced by `--speed`** - required of the executor
    before rung 2 closes. Reason: a test that reads its speed from a global set by hand is not
    a worked example a stranger can run.

## The coordinator's own work between rungs

22. **A copy of the coordinator's own, `coord`.** Reason: every merge is to be followed by the
    tests of the three subjects passing, and the executors' copies are theirs. After the merges
    of rolls 1 and aiming 1 the whole of `tests/` passed in it at `--speed 1` and at
    `--speed working`, one session.
23. **Strays more than one Lua state deep - fixed in the shared stage.** A state now says in its
    own log what it inherited (`scope_strays`), and the next state reads that as well as the
    tracks. Shown live in `coord`: a goblin staged, two bridge reloads with no Begin between,
    then Begin names it as a stray, Clean removes it, Verify is clean a tick later. No test of
    its own is written tonight: it needs two bridge reloads inside a test, and
    `tests/_selftest/clean/stray_after_fresh_begin` covers one state only - a to-do.
24. **A tutorial is a real surprise.** The first fight in a fresh copy opens the game's tutorial
    box inside `tests/_selftest/clean/attack_in_fight`; it closed by itself inside the test. It
    is not allowed in the header: a fresh copy should not show tutorials at all - a to-do for
    the copy's profile.

## Attacks, rung 1 (a fair melee attack inside a fight, and the verb)

25. **Merged** (997cdea, merge b027bab): two tests under `tests/combat/melee`, proven live at
    `--speed 1` and `--speed working` in one session by the executor; a fair `GK.Act.Attack` and
    `GK.Act.Cast` on the client that end on the game's finished event of the cast; a `game_hit`
    row for every hit and miss.
26. **The rung is not closed until the verb has its second unlike case** - sent back for one
    test of `GK.Act.Cast` with a ranged spell attack before rung 2. Reason: rule 1 of the API
    rules asks for two unlike cases before a piece is merged, and rung 3, where a ranged case
    would come by itself, is two rungs away. The merge into the working branch was made
    anyway so that the other two get `game_hit`; nothing leaves the working branch tonight.
27. **The brief corrected - granted as a finding.** Only the raw `Osi.UseSpell` cast always hits
    and costs nothing; `GK.Cheat.Attack` takes the skipping options off and rolls and pays.
28. **The executor's edits of shared files accepted as made**: one `Ext.Require` line in
    `BootstrapClient.lua` for its `Client/Act.lua`, and the sample name in `VerbClassTests.cs`
    (`Attack` no longer refuses). Reason: neither could be avoided by the change it owns.
29. **Lua on the render thread** (`Ext.UI.Defer` callbacks read the ECS while the main thread
    changes it) is taken as the leading explanation of "no character of this client is
    controlled", not reproduced. Rule for the client's verbs from now: no subscribe or
    unsubscribe from a deferred callback - one crash came from it. The fix in `Client/Aim.lua`
    is the aiming executor's: the subscription moved to load now; the reads moved to the main
    thread if the hazard can be shown or the cost is small.
30. **A shared wait on a row - granted as `row()` in the prelude**, not as a shared `_events.lua`.
    Reason: written by hand in a third test, which is the bar the document sets; `require`
    looks beside the test only, so a file could not be shared across folders.
31. **Crash, `attacks`, once**: the game closed after a run of the new verb; Script Extender's
    log: "Assertion failed: global_ != -1". Preceded by `Ext.Entity.Unsubscribe` called from a
    deferred check on the render thread. Restarted by its executor (`down`, `up`).
32. **A pin "not on after 90 ticks"** three runs in a row on that copy just after its restart,
    never later - passed to the rolls executor as a suspected bug of ours.

## Attacks, rung 1 closed; rung 2 half done

33. **Rung 1 closed** with the verb's second case (9584035): `GK.Act.Cast` with Fire Bolt at
    range - refused out of range in the cursor's words with nothing spent, a pinned 1 a critical
    miss, a pinned 19 a hit, the action paid, the outcome equal to the `game_hit` row. Merged
    (601d2a6) with the first half of rung 2: `weapon_state` rows and
    `tests/combat/weapon/after_fight.lua`, live at both speeds by the executor.
34. **The executor's run ended for want of context, mid rung 2; a continuation was started** on
    the same brief, branch, worktree and copy, reading the chronicle its predecessor left. It
    is the same executor, not a fourth. Reason: a subagent's context is finite and a queue of
    five rungs is not; the chronicle on the branch is what carries the work over.
35. **Clean waiting on the weapon's event: the executor may edit that one place of
    `Server/Stage.lua`** - granted, narrowly. Reason: the brief's rung 2 asks for exactly this
    replacement, the executor holds the knowledge of the event, and the edit is reviewed at
    the merge and proven by the self-tests and `stage_clean_cycles` at both speeds.
36. **A finding against the coverage data**: `CombatLeavingComponent` read there as an
    eight-second timer holding the weapon lives about four ticks and holds nothing. Kept in the
    executor's study; the coverage record is not edited tonight - a to-do.

## Aiming, rung 2 closed (what blocks an aim)

37. **Accepted and merged** (7c63e9b, merge 765f8c7): two tests under `tests/aiming/blocking` in
    `TestLevel_Tom` (the sandbox has no geometry), live at both speeds in one session; the rung 1
    tests again at both speeds after the change of `Aim.lua`.
38. **The subscription from a deferred context** is gone from `Aim.lua`: made from the bridge's
    tick on the main thread once a level runs (made at load it never fired). Accepted.
    **Moving the reads to the main thread - not done, accepted as recorded**: no disagreement
    was seen and the move is not small; a suspected bug of ours with one failure as evidence.
39. **`Aiming` naming the kind of refusal (`sight` or `path`) - granted**, on condition that it
    is read from the game's state and not from the localised text. Reason: the rung 2 tests
    compare two texts for being different, which is weak.
40. **An observation verb for the AI-grid tile at a point - granted as a wish**, built when a
    later rung needs it again.
41. **Verify does not see a level object's state** (a door left open) - an open item of the
    shared stage; not built tonight.
42. **The hidden creature** (an incapacitating status given in the chunk that creates it leaves
    it invisible to the party until it moves) - a suspected bug of the game, kept in the study;
    no test is written only for it.
43. **The aiming executor continued in a fresh run**, as attacks was (decision 34), before its
    context ran out.

## Rolls, rung 2 closed (rolls in a real dialogue); a defect of the pin found by the merge check

44. **Accepted and merged** (ab350d9, merge aa74c69): three tests under `tests/rolls/dialogue`
    on the game's own dialogue - the branch taken by the check, a throw again for inspiration,
    a bonus added in the window - all of `tests/rolls` live at `--speed 1` and
    `--speed working` in one session by the executor. `GK.Rolls.Inspiration` / `SetInspiration`.
45. **`dialogue/bonus` at speed 1 only - accepted with its finding as suspected.** Guidance's
    die added in the window was missing seven times of seven at speed 8 and never at speed 1.
    Not yet shown to be the game's own and not ours. The test declares it: **a speed ceiling
    in the header is built** (`speed_ceiling = <n>`; the runner never starts the test faster).
    Reason: the document already decides this declaration; setting the speed by hand in a
    first step hid it from the run's report.
46. **A `game_flag` row - refused as a new kind**: `GK.Watch("FlagSet", 3)` with the `osiris`
    family on already gives the row.
47. **Recorded as suspected bugs of ours, not fixed tonight**: `GK.Gui.Resolve{dialogue='last'}`
    choosing an answer while the one just given had not taken (the dialogue part of
    `Popups.lua`); the host interpreter garbling a text pattern over Cyrillic text (`gsub`).
48. **The check after the merge of aiming rung 2**, from a closed copy, one game per level: at
    `--speed 1` all seventeen tests of `tests/` passed (sandbox and `TestLevel_Tom`); at
    `--speed working` the four tests of `tests/combat` failed at their first pin.
49. **The pin does not arrive from a cold start at speed 8 - a defect of ours, the rolls
    executor's, before its rung 3.** Shown three times of three in `coord`: a fresh start,
    speed 8, `GK.Rolls.Pin{kind='Initiative'}` answers "not on after 90 ticks"; after one pin
    made at speed 1 the pins at speed 8 arrive. The attacks executor had seen it after a
    restart (decision 32): twice in the same place, a finding. Reason for the priority: every
    subject pins, and a rung's "both speeds" proof from a cold start fails on it.
50. **The rolls executor continued in a fresh run** (as decisions 34 and 43).

## The pin's cold-start defect; kit rows carried between game processes

51. **Decision 49 corrected: the cause was not speed.** The rolls executor showed that `Pin` never
    asked for the boost: a kit row "pin Initiative at 100" left open by an earlier session was
    carried into the new log and taken as a pin already on. One pin at speed 1 "cured" it in
    `coord` only because it had another n. Fixed in `Server/Rolls.lua` (94dc966, merge 121af16)
    and proven by the executor from a closed copy at both speeds with a stale pin planted.
    `SetInspiration` failing on a fresh level fixed with it.
52. **Nothing is carried from a log of another game process - built by the coordinator.** `up`
    names the process (`process.txt` in the channel); the log's head records it; a Lua state
    whose previous log bears another name reads no previous rows. Reason: the kits and the
    stage carry what a Lua state left open so that a bridge reload loses nothing, but a new
    process is a new world, and fourteen such rows had gathered in one copy. Shown live in
    `coord`: eight stale kit rows before, none after a restart; two staged rows still carried
    across a bridge reload and cleared.
53. **The first-time tutorials of a fresh profile** (the fight's, the roll window's) are real
    surprises and stay unallowed; one to-do: a copy's profile shows no tutorials.

## Attacks, rung 2 not closed; aiming, rung 3 two of three

54. **Attacks rung 2 is not merged**: `weapon/toggle` fails some runs because the weapon set's
    key is not always taken. Reason: a test that needs a second run to pass is a defect. The
    executor gets one bounded attempt at the cause, else stages the second unlike case of
    `Draw` / `Sheathe` by a cheat step and records the dropped key as a suspected bug.
55. **`game.mark()` is the game's own head - built.** Reason: the executor showed rows of the
    previous test's clean arriving after a mark taken from the files; a mark is now each side's
    boot and last seq as the game says them, and the same for the mark a test starts at.
56. **The ladder waiting on rows that Clean names instead of polling Verify every 250 ms** -
    accepted as the design, not built tonight; an open item.
57. **`GK.Act.Press(event)`** (the bound key of any input event) stays in the executor's file
    tonight; whether it belongs under `GK.Gui` is left open.
58. **Hang, `attacks`, once**: a Fire Bolt cast that never ended (the casting state and the
    unsheath fallback timer stayed) after an out-of-range step at the working speed answered
    "no targeting began (it shows MoveTo)"; every clean after it failed, Escape did not end it.
    Preceded by a fight and a Clean made by hand. Restarted by the executor. Not reproduced.
59. **Aiming rung 3: two kinds accepted and merged** (57fb7b2, merge bd5bdbf) - melee reach is
    the weapon's range plus both hit radii; a ranged weapon's long range is exactly the two hit
    radii beyond its normal range - live at both speeds. **The rung is not closed as two of
    three**: the rule merged with rung 2 (a projectile is refused exactly when a tile on its
    line is `ShootBlockItem`) was later seen not to hold, and a merged test stands on it. The
    executor investigates first, bounded, then restates the rule or marks it not established.
60. **`Aim` always waits its settle** (600 ms and 10 frames) - granted for truthfulness: it had
    answered valid for a target 0.15 m beyond reach. It is a wait on the real clock and stays
    a recorded gap. Announced to the attacks executor.
61. **`GK.Stage.Outfit` not undone by the clean** - a suspected bug of the shared stage; not
    fixed tonight.
62. **The deck was found closed** ("deck closed; every window is back on the desktop" in its
    log, no time, nobody known to have closed it); the four copies run in windows of their
    own. It is left closed. Reason: the owner allowed windows of one's own; opening it again
    would pull every copy into a tile mid-work and change its frame rate from about thirty a
    second to ten - and that very change is the first suspect for the aiming verdicts that
    differed between early and late in the night. The executors are told not to open one.
63. **The aiming executor continued in a third run; the surprises report takes the party from
    the game** (the host had been listed as a stranger when the log held no scope opening).

## The check from a cold start; attacks rung 2 closed; rolls rung 3 closed

64. **The check after decisions 51-63**, from a closed copy: at `--speed working` the four
    tests of `tests/combat` passed from a cold start (the pin's fix holds); two tests failed
    there and one at `--speed 1`:
    - `rolls/dialogue/bonus` at the working speed - a defect of the coordinator's own: the speed
      ceiling was looked at for the first test of a group only (`??=` never evaluated its right
      side again). Found by the check and by the rolls executor at once. Fixed.
    - `aiming/blocking/placed_objects` at both speeds - the rule the aiming executor already
      doubts (decision 59); it stays red on the working branch until that investigation ends.
    Two unit tests were red: one of the coordinator's (the exact mark adds a chunk the test
    counted; the test now leaves it out) and `RollsTests.Once_takes_the_pin_off...`, which
    indexes nil since the rolls executor's changes of `Rolls.lua` - given to that executor as
    the first thing of its next run, with leave to edit that unit-test file.
65. **Attacks rung 2 closed and merged** (7713b65, merge a95a669): `tests/combat` six of six at
    both speeds in one session; Clean waits on the game's own event that the weapon is away;
    fair `GK.Act.Draw` / `Sheathe` by the player's key; the interruptions as one test. The
    owner's hypothesis held on the client's visual side: after a second toggle 0.3 s later the
    logic goes out and back and the model never leaves its sheathed state.
    Reasoned and not shown: the branch of `RestoreMember` for a weapon drawn at the baseline
    and away during a fight (the game puts no weapon away in a fight, so the state was not
    reached). Suspected bug kept: the weapon set's key not taken four times in about ten runs.
66. **The stage's baseline held no weapon - fixed by the coordinator.** The slot names were the
    enumeration's (`MeleeMainHand`); `Osi.GetEquippedItem` knows `Melee Main Weapon` and the
    like (read live in `coord`: the four old names answered nothing, the new ones the host's
    dagger and bow). Found by the attacks executor: its ranged test left the bow slot empty and
    Verify named only a count of boosts.
67. **Attacks rung 3, ranged weapons merged** (70963aa): five ranged weapons as steps of one
    test, each hitting for its dice at their most plus the same modifier. The rung stays open
    for the saving-throw spell, unarmed and off-hand.
68. **Rolls rung 3 closed and merged** (99a6f97, e25c07b, merge cc663fb): three tests - the
    game draws two d20 for every roll and keeps the higher, the lower, or either; a pin holds
    both dice; the window and the server keep the same die. `game_roll` gains `discarded` and
    says `advantage` for passive rolls too. Proven by the executor from a closed copy: eleven
    of eleven at `--speed 1`; ten of eleven at the working speed, the eleventh being
    `dialogue/bonus` under the coordinator's ceiling defect.
69. **`require` reaches a sibling folder - granted and built** (a test's module paths are its
    folder and the one above). Reason: a helper existed twice for want of it.
70. **A process slip noted**: an executor appended to its chronicle with a shell heredoc. The
    coordinator has written this ledger the same way. Rule 10 asks for the editing tools; from
    here on both do.
71. **The rolls and attacks executors continued in third runs.**
72. **The clean puts a weapon back - shown live in `coord`** after decision 66: the host's bow
    unequipped, Verify names "Ranged Main Weapon ... is not what it was", Clean equips it
    again, Verify is clean. Before the fix the same steps left the slot empty.

## Aiming: the placed-object rule restated

73. **Decision 62's suspect cleared: the frame rate does not decide the verdict.** The executor
    varied the cap (4, 10, 30 a second; `gatekit window fps` works without a deck): the same
    course, the same verdict.
74. **The rule, accepted and merged** (3be1571, merge b41c08a): the cursor judges the walk.
    Where the line from the caster is blocked, the game looks for a place to act from and gives
    its verdict on the walk there - out of a fight any walk is taken; in a fight without the
    movement it is refused "not enough movement"; where no place is found the line's own
    reason is given. For about twenty milliseconds the task reads "path blocked" before the
    walk is judged: rung 2's `Aim` answered at the first change and caught it; since `Aim`
    waits its settle the same aims read taken. `placed_objects` is rewritten on this rule and
    passes at both speeds in `TestLevel_Tom`.
75. **Not established, and said so in the test and the study**: which placed object blocks
    which kind of line and why (a barrel the projectile only, a wine barrel both, a door placed
    at run time sight only; neither the AI grid's flag nor the physics rays explain it). Left
    to rung 6. Rung 2's "an object created at run time never blocks sight" is withdrawn.
76. **Recorded as suspected bugs of the shared stage and event log, not fixed tonight**:
    `GK.Stage.Fight` starts no fight when the enemy is already immortal; no `game_turn` row
    with `who` came after a fight began in `TestLevel_Tom` though the host's turn was active;
    `Osi.RestoreParty` gives no movement back inside a fight (a wish: a stage verb for it).
77. **The check of the tree at a95a669 from a closed copy**: at `--speed 1` twenty-eight of
    twenty-eight (with the rewritten `placed_objects`, merged while the run went); at the
    working speed twenty-six - `placed_objects` in its old form, and `rolls/pin/passive` once
    ("the boost was asked for and is not on" for a pin on the staged creature). The second is
    given to the rolls executor as a defect to find before its rung 4 closes. The speed
    ceiling held: `dialogue/bonus` was started at 1 and passed.

## Aiming, rung 3 closed (three kinds of reach)

78. **Accepted and merged** (989f2dc, merge d6ceba1): `tests/aiming/reach/throw_over` - in a
    fight with no movement a throw is judged by its arc (a low barrel half way lets it pass
    where the straight bolt is refused; a tall one near either end refuses it "path blocked",
    never "not enough movement"); clicked, the dagger lands by the goblin and the host does not
    move. All of `tests/aiming` live at both speeds, in `TestLevel_Tom` and in the sandbox.
79. **`Confirm` on the first click of a two-click action answers `ok` with `picked`** - granted
    and announced to the attacks executor. Reason: "the game did not take the click" was false
    for Throw, whose first click picks the object.
81. **Rolls rung 4 closed and merged** (cc7895b, merge 2463ba9): `tests/rolls/save/load` - a
    load draws the dice fresh: it sets the roll manager's base seed to the clock's second of
    the load and starts the stream's count at zero; two loads of one save gave seeds apart by
    the seconds between them and different dice. A cold start of a level always draws the same
    dice (the stream's seed is 1). `GK.Rolls.Streams()` reads the streams. All of `tests/rolls`
    twelve of twelve at both speeds, each from a closed copy, by the executor.
82. **The red unit test was the test's fault** (it read a finished roll after the five ticks it
    is kept for); corrected by the executor in `RollsTests.cs`.
83. **`rolls/pin/passive` failing once in `coord` (decision 77) was a stall of the game**: the
    answer said "not on after 1 ticks" with seven seconds waited - the server ticked once in
    seven seconds, at the working speed, with four games running. **Hang, `coord`, once**;
    cause unknown. The pin's limit is now counted in server ticks.
84. **Every observed answer's limit is of a game that runs - built in the bridge by the
    coordinator.** Reason: the executor's fix covered `GK.Rolls` only, and any verb answering
    through `GK.Observe` failed the same way after a stall. The last look now also waits until
    the looks made would fill the limit at ten a second. Shown live: a limit of 1500 ms still
    ends at 1525 ms on the server and in the UI; the acting verbs answer as before.
85. **Wishes recorded, not built**: the runner taking a new baseline after a test's
    `game.reset()`; `gatekit save` reading a save's components.

## Aiming, rung 4 closed (the area's preview against the blast)

86. **Accepted and merged** (09167ba, merge 1d0a708): three tests in the sandbox, live at both
    speeds - a round area touches a creature whose centre is within the radius plus its hit
    radius, and the preview outlines exactly those the party sees; an unseen creature inside
    burns without being outlined; an unseen creature on the flight line catches the bolt and
    the blast goes off there. The ally the brief's preview "left out" was unseen, not on an
    edge. Not asserted, a probe only: a seen creature on the line lets the bolt fly on.
87. **The observation of whom the party perceives - granted before it was proposed**, with its
    conditions: read from the game's own perception state and not `Osi.CanSee` alone; any party
    member and any creature; a player's knowledge and no more; two unlike cases before use.
    The executor may add that one verb to `Server/Situation.lua`, where `GK.See` lives.
    Reason: rung 5 cannot be fair without it, and a round trip to ask would cost an hour.
88. **The aiming executor continued in a fourth run.**

## The owner's word: not wide, the instruments

89. **The night's aim changed by the owner in person**: no longer to go wide through the queues,
    but to prove that every instrument of the toolkit works, together, with several copies
    running - the tracer, the memory instrument, `decomp at` and `decomp code`, `symbols`,
    `names set`. Marking the machine code is one of the project's key tasks.
    What had happened until then, said to him plainly: no executor had reached the code. The
    descent stood as rung six of six or seven in two briefs and was absent from the third;
    "done" for a rung asked for no instrument and no name; and the coordinator accepted rungs
    by that definition without bringing the descent forward. The tracer had been used once
    (attacks), the memory instrument by nobody, `names set` not once.
90. **The queues re-ordered accordingly:**
    - rolls: rung 5 only as far as it is the instruments' work, then rung 6; rung 7
      (initiative) dropped for the night;
    - aiming: rung 5 cut to the observation of whom the party perceives, then rung 6;
    - attacks: the rest of rung 3, fair movement and the opportunity attack put off; a descent
      its brief did not have - where an attack hits or misses and where its natural die is.
91. **"Done" for a descent**: each answer has its address, our own sentence on what the code
    there does, the instrument's reading that proves it, and a name set - or "not yet, and why"
    with what each tool answered. Each executor also writes, per tool, whether it worked at the
    first call, what was wrong or missing, and whether other copies disturbed it. The first
    thing each settles: whether "who writes this field" (`GK.Memory.Watch`, `Hits`) is built -
    the document on tests and the open items disagree. An executor reports as soon as each
    instrument has been tried once.

## The instruments tried once; the name of a function

92. **"Who writes this field" is built and works** (`GK.Memory.Watch`, then `Hits`), shown by
    two executors independently in two copies with four games running; the open items' line
    that it is done by hand with an external debugger is out of date. `decomp at` and
    `decomp code` answered every address at the first call. The tracer worked at the first
    call. Merged: rolls (33f4144, with `GK.Rolls.SetStreams` and the streams rewound), attacks
    (a3969ce, with the saving throw, unarmed and off-hand tests) - merge d50d730.
93. **The gap both named: nothing in the toolkit could name a function.** `names set` names the
    fields of a layout; a function's name came only from what the symbol database matches by
    itself. The owner, asked, had meant this to exist. **Built by the coordinator**:
    `gatekit decomp name <address> <name> --evidence --does` writes a row of
    `research/coverage/functions.jsonl`; `decomp at` shows it; `decomp names` lists the ledger.
    Refused: a machine's name, a name without evidence, a weaker source over a stronger. Not
    built: `decomp refresh` reading the ledger, so the pseudo-code keeps the decompiler's
    name. First row, from the rolls executor's watch: `bg3_dx11.exe+0x105AAB0`,
    `esv::roll::stream::DrawNumber` - the function that draws a number from a roll stream.
94. **The rule that nothing of the game's code enters a report stays.** The owner offered to
    lift it for this stage; it was not needed: an address and our own sentence carried every
    report and every name.
95. **Defects and gaps of the instruments, recorded, not fixed tonight**: `GK.Memory.Field`
    does not reach inside an array element; no `GK.Memory` verb for a system's address
    (`Ext.Debug.GetSystemAddress` is called raw); a hit's `Before` is not the earlier value; a
    watch on a component's address holds only until the storage moves; the record has no
    nested struct for `Stream`, so its fields cannot be named; the tracer's options are
    documented only in the fork, a change's entity does not resolve, `Flags` is unreadable, and
    a component modified every frame hides its own change when the baseline is subtracted by
    name; `symbols game` and `decomp matches` find none of the Toolkit's named roll functions
    in the game.
96. **Suspected bugs of the shared stage and windows from attacks rung 3, recorded, not fixed**:
    `RestoreMember` restores spells before equipment and leaves a second copy of the weapon's
    attack, which stops the game granting the unarmed strike, and Verify (comparing spells by
    name) calls it clean; Verify names `esv::unsheath::DefaultComponent field_0 = 3` dirty
    after a toggle test; `GK.Gui.Click` does not fall back to a control's own command (the
    dual-wield toggle); the hotbar is not in the baseline. **Hang, `attacks`, a second time
    in the same place** - a finding: an orphaned preview cast of the main-hand attack on the
    server (no targets, the unsheath fallback timer and the spell animation lifetime), no cast
    on the client, after a first aim not taken at the working speed.
97. **The check of the tree after the bridge's first rule (decision 84)**: seven tests failed
    at the working speed and four at speed 1, all on the client's side ("clicked and left the
    targeting", "no weapon_state row", a roll window not seen). Suspect: that rule asked for
    looks at ten a second, which a client drawing few frames cannot give inside the host's
    timeout. The rule was softened (the last look is never one of the first three); whether
    that was the cause is being checked in `coord`.
98. **The cause of decision 97's failures was neither rule of the bridge**: `Confirm` took an
    ordinary one-click attack for the first click of a two-click action (the sign chosen in
    aiming rung 3 shows on both), so the fair verb found no cast although the attack was
    carried out. Shown by the attacks executor's reading of the verb's answer; fixed by the
    aiming executor in `Client/Aim.lua` (the sign is now the flag and a picked entity the task's
    own target no longer holds). Found only by the check of the merged tree: each executor's
    own tests passed on its own branch.
99. **Fresh executors with the whole toolkit and fresh game windows**, by the owner's word, after
    the coordinator had walked one simple case to its end himself: a write watch on the host's
    hit points, the instruction at `bg3_dx11.exe+0x31C2DAE`, `decomp at` naming the function the
    attacks executor had named from its own watch, and the name shown in its caller's code.
100. **Hot names, built by the coordinator at the owner's word**: `decomp vars` (a function's
     variables and parameters), `decomp field` and `decomp struct` (fields of a structure that is
     no component's), and `decomp code` writing a field by name where a person tied a variable to
     its structure and the arithmetic is in bytes, the offset kept beside the name. Reason for
     the narrowness, put to the owner and agreed: a substitution by text over every pointer would
     put a name where the same offset is another structure's; a wrong name is worse than an
     honest offset. The further step - types fed to the decompiler itself - is in the summary.
101. **What the first real use showed of them**: the substituted code read true where it applied;
     the decompiler reuses one variable for several things, so a variable's name true on one line
     was false further down and was taken off again - a limit of naming variables by text; in one
     place a substitution that should have applied did not (`*(longlong *)(manager + 0x18)`), not
     yet looked at; `Moved` is set when the hits are read and marks the good hits before the move
     as well - only the fork can stamp the move, the contract now says to tell them apart by frame.
102. **The dice, settled by the descent**: the natural die of an attack is kept in the hit's
     condition rolls and was readable all along - `game_hit` now carries it, no addition to the
     fork is wanted (it is the die after a pin bounded it; the drawn value is overwritten); the
     comparison with the armour class and the verdict of a check are found, named and shown by
     a freeze; a stream's seed is the base seed plus the stream's id. Three declarations of Script
     Extender read wrong against this (`SpellCheck` is a roll's verdict; `Frame` of a stream is
     its id; `LastFrame` is the last id given) - suspected, not yet held to the bar of a bug.
103. **Recorded, not fixed**: a bridge reload in the middle of an open stage scope loses the
     scope (pins stay on, Clean and Verify find no baseline); after a load the new Lua state does
     not run the memory instrument's tick hook, so a watch stays armed with nobody polling it;
     the ring of hits fills with a copy made every frame; a return chain is six deep at most; a
     game closed once when three write watches were armed from a tick handler just after a clear.
104. **By the owner's word: no claim about a door without its geometry.** A test that says a
     placed door, or the level's, does or does not block a line first shows that the leaf stands
     across the line between the caster and the target, closed and covering it. None of the
     tests asserted the placed object's rotation; the claims of decision 75 about doors stand
     only once that check holds. Given to the aiming executor.
105. **And no claim without the door's state**, the owner's second point: a door in the state
     Open may let every aim through. The state of the door placed at run time was never read; it
     was taken to be closed. Each door is to be tried closed across the line, opened, and closed
     again, its state read from the game and held in the test's precondition.
106. **The doors, corrected - the owner was right on the first point.** The door of
     `placed_objects` had been made unrotated and lay along the line it was meant to cross; both
     doors do come into the world closed. With the leaf across the line: a closed door blocks
     sight and path, an open one neither, the level's and a placed one alike. Withdrawn: "a
     placed door blocks sight only", "the level's door differs from a placed one". The bottle
     rack hung above the line and is no longer asserted. The stage's spawn takes no rotation - a
     gap of the shared stage, recorded.
107. **Aiming's descent accepted and merged**: who writes an AI-grid tile and what in a placed
     object makes it block (the bound's flags), with three functions and ten fields named; the
     cursor's verdict traced to its field and its writer on the client, the deciding bit of the
     error mask not found - a function's return value cannot be watched. `Confirm`'s fix
     (decision 98) proven by the executor at both speeds in the sandbox and in `TestLevel_Tom`.
108. **By the owner's wishes, written into the summary for the next proof of concept**: resources
     given back inside a turn by a cheat of the stage, so that combat tests neither end turns nor
     wait out enemies; a test accounts for its own time and removes the waits its rule does not
     need; a fair `AimBest` leaves the unseen out of the line of the aim as well as the area.
109. **The check of the tree after `Confirm`'s fix**, from a closed copy: thirty-seven of
     thirty-seven at `--speed 1`; thirty-six at the working speed - `combat/weapon/interrupted`
     ("no weapon_state row"), the known key that is not always taken (decision 65). One unit
     test was red by the coordinator's own doing - the new ledgers read and wrote JSON outside
     the core, which a guard counts - and was mended.
110. **By the owner's word the night is wrapped up after the last executor's report**: every
     branch merged, every game closed, the summary finished, and a full report handed to the
     session that started the coordinator.
80. **A fair observation of whose turn it is** - granted as a wish, built when a later rung
    needs it again, beside what it reads and not under `GK.Stage`.
