# The proof of concept: summary of the night of 2026-11-15

Three mechanics - rolls, attacks, aiming - each by an executor of its own under one coordinator.
Every decision with its reason is in [decisions.md](decisions.md); each executor's chronicle is in
[reports/](reports/); the studies are in `research/studies/`. Nothing is pushed; the working
branch is `claude/poc-c332c2`, the executors' branches `poc-rolls`, `poc-attacks`, `poc-aiming`.

The state it was left in: every executor's branch merged into the working branch; from a closed
copy, one game a level, all thirty-seven tests of `tests/` passed at `--speed 1` on the final
tree, and the unit tests are green (`dotnet test`). At the working speed the tree before the last
merge passed all but `combat/weapon/interrupted`, whose key is not always taken; the final tree
was not run again at the working speed by the coordinator - the last executor ran its own tests
there. Every game is closed and no deck is open.

The night had two halves. Until the owner's word (decision 89) the executors went through their
queues; after it the aim was the instruments: tracing, reading and freezing memory, the place in
the machine code, a name for it.

## Rolls

Asked: the queue of the rolls brief, seven rungs.

| Rung | State | Proven how |
|---|---|---|
| 1. The pin's tests in the tree | closed | five tests, live at both speeds |
| 2. Rolls in a dialogue | closed | three tests on the game's own dialogue, live at both speeds; `dialogue/bonus` at speed 1 only, by a declared ceiling |
| 3. Advantage and disadvantage | closed | three tests, live at both speeds from a closed copy |
| 4. A save, then a load | closed | one test, live at both speeds |
| 5. The streams to full repeatability | in part | `tests/rolls/streams/rewind`, live at both speeds: a rewind repeats the rolls; choosing a die is not reached; the fight's streams not tried |
| 6. The descent into the code | done for the questions asked | memory watches, `decomp at`, names (below); what a load does to the stream ids is a hypothesis |
| 7. Initiative | dropped for the night | by decision 90 |

What is understood:
- Karmic Dice overrides a pin on the rolls it weights; the earlier proof used passive rolls, which
  it does not weight. `Pin` switches it off for the scope.
- The game draws two d20 for every roll; advantage keeps the higher, disadvantage the lower; a pin
  holds both.
- A load draws fresh: the base seed is the clock's second of the load. A cold start of a level
  always draws the same dice.
- A die is a function of its stream's seed and count; writing them back repeats the rolls
  (`GK.Rolls.Streams`, `SetStreams`). Choosing a die is not reached.
- In the code, each named with its evidence: the draw of one number; the roll of a formula; the
  doubling of the d20 and the keeping of one; the clamp of a pin's boosts; the copy of a stream
  and of all streams; the system that writes the base seed; the making of a stream, whose seed is
  the base seed plus the stream's id; the function of the roll system that writes a check's
  natural die, discarded die and verdict (a natural 20 passes, a natural 1 fails, else the total
  against the difficulty).
- Three declarations of Script Extender read wrong against this: `SpellCheck` of a requested roll
  is its verdict, `Frame` of a stream is its id, `LastFrame` of the manager is the last id given.
  Suspected: not yet held to the bar of a confirmed bug.

Not done, and why: the fight's streams rewound (not tried); how a load sets the stream ids back
(a hypothesis); Guidance's die lost above speed 1 is not shown to be the game's own; one game
closed when three write watches were armed from a tick handler (not reproduced).

## Attacks

Asked: the queue of the attacks brief, five rungs.

| Rung | State | Proven how |
|---|---|---|
| 1. A fair melee attack in a fight, and the verb | closed | tests live at both speeds; the verb tried on melee and on a spell attack |
| 2. The weapon after a fight | closed | tests live at both speeds; one case staged by a cheat because the weapon set's key is not always taken |
| 3. The other attacks | closed but for two items | ranged, saving throw, unarmed, off-hand: tests live at both speeds in one session; the whole folder did not pass in one session for two intermittent weapon-toggle defects |
| 4. Fair movement | not started | put off by decision 90 |
| 5. The opportunity attack | not started | put off by decision 90 |

What is understood:
- Only the raw `Osi.UseSpell` cast always hits and costs nothing; `GK.Cheat.Attack` rolls and
  pays. The brief said otherwise.
- A critical doubles the dice and not the modifier; a spell's save is decided by the total, not by
  a natural 1 or 20; an unarmed strike with a negative modifier deals 1; the off-hand attack keeps
  a negative modifier.
- The weapon's state changes at once on the server; the animation is only asked for beside it. The
  owner's "weapon on the back at once" holds on the client's visual side: after a second toggle
  0.3 s later the model never leaves its sheathed state.
- In the code: the function that writes the hit points; from the other side, the same clamp of a
  pin's boosts the rolls executor reached - shown by a freeze of the boost's value; the function
  that rolls an attack against the armour class and writes the hit's flags, and the comparison
  itself - shown by a freeze of the target's armour class, which turned the same die from a hit
  into a miss; where damage dice are drawn.
- **The natural die of an attack is kept**, in the hit's condition rolls, as a saving throw's is,
  and was readable through Script Extender all along: `game_hit` dropped it. `game_hit` now
  carries `attack = { kind, ac, natural, total, critical }`. No addition to the fork is wanted.
  It is the die after a pin bounded it; the value as drawn is overwritten.

Not done, and why: fair movement and the opportunity attack (put off); whether a damage roll is
one draw a die; `game_hit.attack` is shown live and by unit tests, not yet by a test of the tree.

## Aiming

Asked: the queue of the aiming brief, six rungs.

| Rung | State | Proven how |
|---|---|---|
| 1. A clear aim, told truthfully | closed | three tests, live at both speeds |
| 2. What blocks an aim | closed, then corrected twice | the placed-object rule restated (the cursor judges the walk); then the doors, by the owner's two points - the placed door had lain along the line; live at both speeds in `TestLevel_Tom` |
| 3. Three kinds of reach | closed | three tests, live at both speeds |
| 4. The area's preview against the shape | closed | three tests, live at both speeds |
| 5. A fair `AimBest` | cut to its core | `GK.See.Perception` built and tried by probes; no test; `AimBest` itself not built |
| 6. The descent into the code | done in part | who writes a tile and what makes an object block: found and named; the cursor's verdict: its field and writer found, the deciding bit not |

What is understood:
- A target action needs sight, a projectile a free path, and the cursor judges the walk: where the
  line is blocked the game looks for a place to act from and gives its verdict on the way there.
- Melee reach is the weapon's range plus both hit radii; a ranged weapon's long range is exactly
  the two hit radii beyond its normal range; a throw is judged by its arc.
- A round area touches a creature within its radius plus the creature's hit radius; the preview
  shows only what the party sees, and an unseen creature on the flight line moves the blast.
- "Invisible" is one component on the creature; what each observer sees is a set kept on the
  creature seen, written by the sight system.

- A closed door across the line blocks sight and path, an open one neither; the level's door and
  a door placed at run time alike. The earlier "a placed door blocks sight only" came from a door
  made unrotated, its leaf along the line; the tests now hold every placed object's box against
  the line, the leaf's angle and the door's state in their preconditions.
- What makes a placed object block is in its bound's flags: one for a projectile (absent when the
  template can be shot through), which shows on the AI-grid tile, and one with a vision blocker
  for sight, which has no tile flag. A barrel has the first, a wine barrel and a closed door
  both, a crate neither - the cursor's verdicts exactly. The three functions of the AI grid that
  write a tile are named. Read and watched, not yet asserted in a test.
- With a seen creature on the flight line the caster steps aside to a clear line before casting;
  with an unseen one it casts from where it stands and the flight stops at that creature - a bolt
  and a blast alike.

Not done, and why: which bit of the cursor's error mask means "path blocked" or "not enough
movement" (the mask is a function's return value: nothing in memory to watch); the fair
`AimBest` (cut; its rule is below).

## The instruments

The chain the owner asked for runs to its end and was run by three executors and the coordinator
independently, with four games up:

1. `GK.Memory.Watch`, the action, `GK.Memory.Hits` - the instruction that writes a field.
2. `gatekit decomp at` - the function that holds it; `decomp code`, `symbols callers` - what it does.
3. `gatekit decomp name` - a name with its evidence and our sentence; shown at once in
   `decomp at`, `decomp names` and the pseudo-code of every caller.

Built during the night for it, by the coordinator:
- `decomp name`, `decomp names`: the ledger of functions named by evidence
  (`research/coverage/functions.jsonl`). Nothing could name a function before.
- `decomp vars`: a named function's variables and parameters.
- `decomp field`, `decomp struct`: the fields of a structure that is no component's
  (`research/coverage/structs.jsonl`).
- `decomp code` lays all of it over the pseudo-code as it prints, with no pass of the decompiler:
  function names in every caller, a function's variables, a structure's fields as a legend, and a
  field by name in the code where a variable was tied to its structure and counts bytes.
- `GK.Memory`: a watch on reads refused (it closed the game), a field with no single size refused
  with the reason, `Moved` on a hit whose field has moved, `System`, `Element`.

Count the ledger with `gatekit decomp names`.

What the first real use showed of the hot names: each worked at the first call for all three
executors; the substituted code read true where it applied. The decompiler reuses one variable for
several things, so a variable's name true on one line was false further down and was taken off - a
limit of naming variables by text. In one place a substitution that should have applied did not
(`*(longlong *)(manager + 0x18)`); not looked at.

What does not work or is missing:
- A watch on reads closes the game (the fork's handler reads the watched address and traps again).
  Three crashes. Refused in `GK.Memory` now; the fork is not mended.
- A hit's `Before` is the value at the slot's previous hit. `Moved` is set when the hits are read
  and so marks the good hits before the move too. A return chain is six deep at most. The ring of
  hits fills with a copy made every frame. After a load the new Lua state does not poll a watch
  left armed.
- `GK.Memory` is the server's; the client is reached only through raw `Ext.Debug` - which works,
  a client write watch included. Nothing reads a function's return value. No verb reaches an
  AI-grid tile: its address was walked by hand from Script Extender's layout.
- The tracer: its options are documented only in the fork; a change's entity does not resolve;
  `Flags` is unreadable; a component modified every frame hides its own change under a baseline
  subtracted by name. `gatekit test --trace`, which the documents describe, does not exist.
- `symbols game` and `decomp matches` pair none of the Toolkit's named roll functions with the game.
- `decomp refresh` does not read the two ledgers; `decomp grep` does not find a given name.

### Further, not built

Types in the database itself. The overlay writes a field by name only where a person tied a
variable to a structure; every other pointer keeps its offset, because nothing ties it to the
structure, and a substitution by text would put a name where the same offset belongs to another
structure. The decompiler, given the structure, writes every access rightly by itself - through
casts, pointer arithmetic and copies. So the further step is to feed `structs.jsonl`, the layouts
and the per-function ties (`decomp vars --struct`) into the decompiler's project as real types and
function signatures, and to re-decompile only the functions they reach: `decomp refresh` reading
the three ledgers, incremental by function. Two things would then follow without more work:
`decomp grep` finds the names, and a tie made in one function reaches its callers and callees
through the signatures. The hot overlay stays as the first look, minutes before the refresh.

## For the next proof of concept

**Resources given back inside a turn** (the owner's wish). In every test with a fight, and in
general, the actor gets its action, bonus action, movement and reaction back by a cheat step, so
that a test with several attacks does not end its turn and sit through the turns of enemies that
are to do nothing. Where it stands:
- The tests end turns today: `attack_by_die` and its kin wait for the host's next turn to have the
  action again, and stage a second enemy only so that a kill does not end the fight.
- The way is known and was used once: the aiming executor wrote the amounts of the action
  resources back to their maxima on the server, through the ledger, and the game took it
  (`tests/aiming/blocking/placed_objects`). `Osi.RestoreParty` does not give movement back inside
  a fight.
- To build: one cheat verb of the stage (`GK.Stage.Refill{ who, action, bonus, movement, reaction,
  slots }` or the like) that writes the resources of any creature, tracked so that the clean puts
  the earlier amounts back, answering on the game's own row that the resource changed; then the
  combat tests rewritten to refill between cases instead of ending turns. It is a cheat and is
  declared one: a test whose rule is that an attack spends the action still reads the spending
  before it refills.
- Beside it: holding the enemies' turns altogether (an enemy's AI held for the whole test) is the
  directing-creatures case of the open items; with a refill most combat tests need no enemy turn.

**A fair `GK.Gui.AimBest` is blind exactly as a player is** (the owner's amendment; the verb is
not built - `GK.See.Perception`, which it stands on, is). The fair search leaves the creatures the
party does not see out of everything it weighs: not only out of who an area would touch, but also
out of what stands on the line of the aim. It does not look for an unseen creature on the flight
line and does not steer round one. So an aim it chose may be stopped on the way by an enemy it
could not count - the bolt, or the whole blast, lands there and not where it was meant - and that
is the right outcome, for a single target and for an area alike. A test of it: one scene where the
fair choice and the cheat's differ because of an unseen creature inside the area, and one where an
unseen creature on the line takes the hit the fair choice meant for another.

**A test accounts for its own time** (the owner's wish, for the document on writing tests). A
test finds, inside itself, the longest waits that have nothing to do with its rule, and removes
them. The example he gave: the tests of a roll inside a dialogue wait for the speaker's first line
to end before they go on to the roll, though the rule is about the roll.
- What exists: the runner times every step and prints it under the verdict; `results.json` holds
  the seconds of each step. Nothing reads them for this.
- To add to `docs/writing-tests.md`, as a step of finishing a test: read the steps' times, name
  the longest, and for each say whether the rule needs it. A wait the rule does not need is
  removed - a line skipped the way a player skips it, a scene staged closer to the moment under
  test, a row waited for instead of a state read until it changes - or it is recorded as a gap
  with the reason it cannot go.
- To build, small: the runner names the slowest steps of a run and the time spent inside waits
  (`game.wait`, `row`, `game.sleep`) apart from the time spent in the game's own answers, so that
  the longest wait is seen without reading the test.
- First places to look: the dialogue tests of `tests/rolls/dialogue` (the first line before the
  check), the staging of the talker kit (seven seconds once), `Aim`'s settle on every new aim, the
  turns ended to get an action back (above).

## Surprises in the event log

The runner lists, after every test, a blocking state the test did not ask for, a creature not of
its cast, and a row the host could not place; a header or a `_suite.json` allows one in a line.

Listed over the night, by `"sort": "<kind>"` in the `results.json` of every run of 2026-11-15
under the copies' `runs` folders: 68 blocking, 2 participant, 0 unplaced.

- Real and meant, now allowed where they belong: `combat_started` in tests that start a fight,
  `dialogue` in tests inside a dialogue, `loading` in the save test, the seven states of
  `kits_and_attention`.
- Real and not meant: the game's first-time tutorials in a fresh copy (the fight's, the roll
  window's). Not allowed anywhere; a fresh copy should show none.
- Real: a `reaction` window in `event_log` at the preflight.
- Noise, removed during the night: the null uuid as a participant; the host as a stranger when the
  log held no scope opening; a dialogue kind not counting as naming the dialogue.

Recommendation: not yet a failure. First the tutorials have to go from a copy's profile, and a
test that reads a blocking state through the game instead of the event log (most of the older
scenarios) has to name it. After that, `blocking` and `unplaced` can fail a test; `participant`
has listed nothing real yet and should stay a report until it has.

## Crashes and hangs

| Copy | What | Preceded by |
|---|---|---|
| attacks | crash, "Assertion failed: global_ != -1" | `Ext.Entity.Unsubscribe` from a deferred UI callback |
| attacks | crash, no log | a read watch on a pin's boost, then a fair attack |
| rolls | crash, twice, no log | a read watch on a pin's boost, then a passive check |
| rolls | crash, no log read | three write watches armed from a tick handler just after a clear, a check against an unreachable difficulty |
| attacks, aim | hang, three times, the same shape | an aim that read "move to" or was not taken at the working speed; an orphaned preview cast stays on the server, no clean removes it, only a restart |
| coord | stall, one server tick in seven seconds | the working speed, four games running |

The deck was found closed at 04:15 with an orderly line in its log; who closed it is not known.
The copies ran in windows of their own from then on.

## What the night says about the toolkit

Where the time went:
- The shared code. About half of the first half of the night went to defects of our own: a pin
  taken as on from a row of an earlier session; a stage baseline that knew no weapon; a mark
  behind the log; limits on the wall clock; a speed ceiling looked at once; `Confirm` taking a
  one-click attack for the first click of two.
- An executor's context. It lasts a rung or a rung and a half; each executor needed four or five
  runs. The chronicle on the branch carried the work over each time.
- Both speeds from a cold start. Each of the defects above showed only there or only after two
  executors' work met; the check of the merged tree in a copy of the coordinator's own found
  three of them.

The rule hardest to keep: events, not waits. `Aim` waits a settle on the real clock; the ladder
polls Verify; several tests read a state until it changes. Each is recorded as a gap.

What the API lacked: a way to name a function; hot names of any kind; a fair observation of whose
turn it is; resources given back inside a fight; a verb for a system's address.

## Nothing lost

Every finding, bug, wish and to-do of an executor is in its chronicle under
[reports/](reports/) and in its study; every one that concerns shared code is in
[decisions.md](decisions.md) with its number and, as an open item, in
`research/coverage/decisions-and-open-items.md` under "Found by the proof of concept".
