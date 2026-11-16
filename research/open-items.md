# Decided and open

The one ledger of what is decided and what is still to do. An item is one or two sentences; it
leaves when it is done, with the commit that did it. A rule that a document owns is not restated
here: the item names the document.

## Decided

### Owned by the documents

- The words `fair`, `cheat`, `observation`, `player`, `omniscient` and `verb`, and the four rules
  of the API: [docs/api-rules.md](../docs/api-rules.md).
- How a test is written, run, cleaned and judged, the bar for a bug, and the method of studying a
  mechanic: [docs/writing-tests.md](../docs/writing-tests.md). What is decided there and not built
  is its section "Decided, not built".
- Copies, the home, the clean game (`gatekit game verify`, the build every database records), the
  event log and attention, venues and the reset ladder: [docs/concepts.md](../docs/concepts.md).
  The bounds of the event log: [docs/contracts/bridge-protocol.md](../docs/contracts/bridge-protocol.md).
- The deck: [docs/design/deck.md](../docs/design/deck.md). Offers to upstream Script Extender:
  [se-fork/upstream/README.md](se-fork/upstream/README.md).
- The rules of the system descriptions and their claims: [coverage/systems.md](coverage/systems.md).

### Decided here

- A kind of event that can be known by a player at one moment and not at another is `omniscient`
  until a decision per row exists.
- In GateKit's own copies the bridge accepts the game's first-launch agreements (the EULA,
  Larian's mod policy): the owner's standing consent covers them. Never in the installed game.
- Playing as the other side replaces effects put on an enemy wherever it can.
- Surviving a crash is three pieces: a watchdog that notices a dead or hung copy at once, a dump
  whose faulting address `gatekit decomp at` turns into a function, and a warm standby copy in the
  same venue. The standby is for dangerous research (a component written for the first time, an
  unmapped function called, a new verb's first runs, anything through memory) and for unattended
  runs, not for finished tests.
- The archive keeps the previous generation, superseded databases, records, and conversation
  logs, session notes, plans and pictures. Leftovers of the old stand are deleted without asking;
  the decompiled databases and the archive are never deleted.
- In the Script Extender fork a layout changes only after a check in the running game, and each
  change is one commit with a description fit for a pull request.

### The cases of the proof of concept

One session a case; no two share files or a game copy. No case session starts before the owner
says so.

| Case | What settles it | State |
|---|---|---|
| Rolls decided in advance | the natural die set for checks, saves, attacks, damage, initiative; the roll window as an event on opening and on closing with its result | the pin and the roll in the event log are built (`GK.Rolls`, `tests/_selftest/rolls_pin.lua`); the descent into the code that draws a number is open |
| Light | logical state read at points; changed by fair means and by cheat; one consequence of the rule checked; rendered against logical answered | not started |
| The main menu as a venue | cold start, first-launch screens, menu, load, new game, a level, back; no fixed wait; each transition timed | a start reaches the main menu past the splash screen; the venue is not started |
| Directing creatures | a neutral creature walks a route while the actor acts; an enemy's AI held for a turn and released; marked cheat | not started |
| Acting as a creature | accepted only when an opportunity attack fires from the creature's own move; a study first | not started |
| Appearance | read on any creature, applied to another built differently, read back equal, survives save and load; its format is the roster's | after reading and players, then NPCs, then save and load |

Rolls and attacks are two cases. **Rolls** owns the dice: the pin with its undo, the roll window
as events, the streams and their seed, and the code that draws a number. **Attacks** owns the
fair path of acting: `GK.Act.Attack` and `Cast` on the player's own path, and every kind of attack
(melee, ranged, spell, unarmed, off-hand) shown to roll, to pay its cost and to obey a pinned die.
Attacks goes before the combat cases and before acting as a creature, which stand on a fair
attack.

## Open

### High priority

- **The registry of our reading of the machine code.** The aim is the complete description of
  the game's machine code, and it lives in one registry in the repository from which every other
  store is derived; the decision and its rules are in [registry/README.md](../registry/README.md).
- **Input.** Where input enters the game, by the instruments; then `tests/input/lock` - every
  outside signal kept out of the game (mouse, keyboard, gamepad and the rest), with a state that
  can be read, undone by the ledger, our own fair actions still passing - and
  `tests/gamepad/state`, `switch-gameplay`, `switch-menu`: which interface is on, the switch each
  way, why any input of the mouse throws it back, a verb that holds the interface wanted, and
  where in the machine code the switch lives. The first step towards more than one player in one
  game. The first run with a real device is made with the owner there. This work goes to a
  session of its own, started on the owner's word and not before.
  A case from a night of unattended work, which the lock must cover: the main screen was switched
  off, the copies opened stretched over a small spare screen, and the machine's real pointer lay
  inside every copy's frame. The pointer drags systems of the game with it - what an aimed
  attack points at, the preview of a walk, hover highlights - so the player's own input sent by
  the toolkit may aim where the pointer rests. It was got round by hand (a smaller window in the
  middle of the screen, the pointer parked in a corner). The lock is to make a copy indifferent
  to where the real pointer is, and a copy is to say when the pointer is inside its frame.
- **No more strictness than the work needs.** The frames already set are enough; a new rule,
  check, schema or refusal is added only where it protects one of the two key products - the
  tests and the registry of the machine code - and those two are held strictly. Everywhere else
  (documents, commands, the record, working notes, the layout of the repository) the lighter way
  is taken, and a rule that costs more in upkeep than it saves is removed. Before a check or a
  rule is added, the question is what it protects and what it costs each time something changes.
- **The self tests are removed and written anew, like the rest.** `tests/_selftest` holds what
  checks the toolkit in a live game rather than a rule of the game - the channel, the event log,
  the stage's clean and verify, the steps' refusals, the memory instrument, the console, the
  fork's surface. They are a mix of hurried checks of several generations (former scenarios
  among them) with sleeps of their own. Each is to be either a unit test (where no game is
  needed), a proper test of the toolkit under the rules of `docs/writing-tests.md`, or dropped.
- **No numbered sections in the documents - among the first things to do.** The contracts number
  their sections (`cli.md` 2.13.1, `lua-api.md` section 17) and code comments, help texts and
  other documents cite those numbers, so adding or moving one section renumbers many and every
  citation has to be chased: edits that change nothing. Headings lose their numbers; a section is
  cited by its title as a link (`cli.md#the-test-command`), which a link checker can hold; a
  citation from code names the document and the section's title.
- **The in-game console knows the API as the editor does.** Its completion asks the live tables
  (the `GK_Complete` channel), so it gives names and nothing else. It is to show, for each
  variant, what `lua-types/GateKit.lua` already says: the signature, the types of the arguments
  and of the answer, the side, and the sentence of what it does - the comment in a small window
  on hover, and beside the variant while typing. One source: the types file (or what it is
  generated from) is shipped to the console, never a second description kept by hand.
- For later, not to be forgotten: a panel in the game, built with Weave, where a player or a
  developer changes things live - the game's speed as a whole and by situation (own turn, an
  ally's, an enemy's, exploration, dialogue), and other commands of use to both.
- **The event log in the in-game console.** The rows of our event log are to be read there as
  they come. To decide: one stream with what Script Extender itself prints, or the console's
  own filters for `GK` (by family and kind, as the registry of event kinds has them).
- **Saving, `tests/saveload`**: everything about saving and loading and about the saves
  themselves lives in this folder. Its first test is `save`: the game is in a state of its own
  while it saves (the turning mark at the top left; another save is refused meanwhile), and that
  state is to be logged sharply, its beginning and its end - the machine code most likely holds
  it as plain states. Covered: the quick save by its key (F5), better by the trigger in the code
  if it is found, and the save from the menu, where the menu is only the way in and what counts
  is what follows the "Save" button. The save's file is checked to appear. After a reset and a
  second save the file is compared with the first; where they differ the difference is studied
  (timers, fresh identifiers), without fighting for an identical copy. No inventory of the
  file's contents here - other tests do that. For the study: the structure of a save is widely
  described on the internet and handled by many mods.
- **Resources given back inside a turn** (`GK.Stage.Refill`): a creature's action, bonus action,
  movement and reaction, tracked and answering on the game's own row, so that a test with several
  attacks neither ends its turn nor sits through the turns of enemies.
- **A test accounts for its own time.** The runner names the slowest steps and the time spent
  waiting; the tests of the first night are then gone over once for waits their rule does not
  need - first the dialogue tests (the speaker's first line before the check), the staging of the talker fixture, the settle of every new aim, the turns ended to
  get an action back.
- **The fork's instruments.** Built and the default for new copies: a watch on reads and the
  clearing of watches no longer close the game, a hit carries the value before the write;
  unsubscribing inside a callback is safe; the tracer is filtered by entity and component, gives
  a change's entity, its count and order within a flush, and the system that was updating;
  watches and freezes are owned by their side and polled by the extender itself; a crash leaves
  a dump in a folder of ours with the faulting address beside it. The bridge's side is built
  over it: `GK.Memory` on both sides, `GK.Trace`, `gatekit test --trace`, `gatekit crashes`.
  Left: a dump of a hang (the extender has no call that writes a dump of a running game); the
  `crash.txt` of a test that fails after a death is not shown live.
- **Four instruments of the fork wait for a person.** The work on them was stopped by the
  model's safety measures and was not attempted another way: a size for every field with paths
  below the top level and a watch over a range; which fields a traced change touched, with
  before and after; addresses for what is not a component (an object behind a proxy, an element
  of a container, a tile, a symbol); finding a field by its value. What is to be written by hand,
  in the fork, in this order:
  a size beside every registered property (the property macros in
  `Lua/Shared/Proxies/LuaObjectProxies.cpp`, carried through inherited properties, and a `Size`
  in the raw accessors of `Lua/Shared/LuaPropertyMap.h`); the address and size of an element of
  an array (`Lua/Shared/Proxies/LuaArrayProxy.h`) and of a value of a map (`LuaMapProxy.h`);
  then, not begun, a path below the top level resolved to an offset and a size, and the
  address of an object behind any proxy, of a tile of the navigation grid and of a symbol.
  Which fields a traced change touched stands on those sizes. A watch over a range with the
  size given by the caller does not depend on any of it and can be built at once.
- **Three declarations of Script Extender suspected wrong,** each a rename with the old name kept
  as an alias, each lacking a test on two bare copies for the bar of a confirmed bug: the field
  it calls the spell check of a requested roll holds the roll's verdict; a roll stream's "frame"
  is the stream's id; the roll manager's "last frame" is the last id given.

- **Found while the bridge was settled, open:** a client verb's read of the controlled character
  from the interface's context can come back empty at a fight's start, so `Aim` begins a tick
  later (a recorded gap) - the cure is moving the verbs' reads of entities to the main thread;
  a weapon toggle pressed within a server tick or two of the previous one is dropped by the game
  (where - the client's input or the server's request - is not read), so the weapon test of the
  first tree that interrupted a toggle declared a ceiling of speed 4; the server's weapon state
  changes at once and the animation is the client's show only, so nothing is to wait for it;
  the server's preview cast left after an aim at a ground point was not reproduced, and the
  game's own way out of it is not studied; what reads field 0 of
  `esv::unsheath::DefaultComponent` is not found; the staging delays of `Server/Fixtures.lua` and
  `Server/Stage.lua` count ticks now, each a recorded gap whose game event was not looked for;
  who spends inspiration in a party of two; strays through two resets; `Confirm` on an item of
  two clicks; the scripted-attack test of the first tree failed once in four runs with a hit read at a
  pinned 1 - suspected: with the action refilled the second cast follows the first in the same
  turn, and the first cast's `game_hit` row comes after the mark, so a hit is to be matched by
  its cast and not by a mark (not proven).
- **Rough edges found by the smoke run of the new tooling** (every basic function ran; these are
  what a first user trips on):
  - Why two `up` of two copies at once each failed twice without the bridge, with a decompiler
    serving beside them, is not known: it did not come back in three rounds of two copies with
    the bridge rebuilt. A start without the bridge now says what the game process and Script
    Extender's log show (`StartCheck`); its reading is what to look at when it comes back.
  - The other verbs of `GK.Stage` that change a creature - `Status`, `Unstatus`, `Kill`,
    `Resurrect`, `Immortal` and the like - still answer a bare `true`; each is to answer what the
    game made of it, as `Damage` and `Heal` do.
  - An action and the `GK.Trace.Step` or `End` after it in one chunk: the action's changes are
    applied at the next flush and fall into the next step (after `End`, into none). A cut made at
    the flush after the call - an observed answer - would give them the step they were made in.
- **Cases inside a test** - built (`case(name, fn)`, `before`, `-k <test>/<case>`); as designed: one level below the file, a case (the bow, the
  crossbow) holding steps; cases marked independent each run from a clean scene, are reported by
  name, and fail the test if any fails. Groups above the file are the folders.
- **A copy's build and the default build are one record.** `gatekit se install --instance <copy>`
  also makes that build the home's default, so installing into one copy changes what every new
  copy gets. They are to be two records.
- **Waits on the real clock and timers of the bridge's own** (rule 3 of
  [api-rules.md](../docs/api-rules.md)), each a defect: in `Server/Act.lua` the job deadlines,
  the arrival and stuck checks, the "nothing happened" window and the cast-flag window; the
  real-second boosts of `Server/Speed.lua`; the windows and retries of `Server/Stage.lua`; the
  fixed delays of `Server/Fixtures.lua`, which `Ext.Timer.WaitFor` rounds up to a tick; the timers of
  the client's windows; in the host `game.sleep` (a thread sleep) and the polling of `Wait` and
  `Answer` in `src/GateKit.Testing/GameApi.cs`; the sleeps in `tests/_selftest/`. Each becomes a read
  from one place on the tick, alive only while something waits for it.

### Acting as a player

- For later: the reading of the scene (`GK.Situation`) knows where the actors - characters and
  others - stand at the moment of the snapshot only. It is to say as well that an actor is
  moving and where to. The fair reading gives no movement of an enemy the party does not see.
- To build, in this order: a fair attack and cast as one helper that waits on the event log; fair
  movement (the player's own move order, which provokes an opportunity attack); fair use, pick
  up, loot and talk through the game's own clicks. Until then each `GK.Act` name answers that it
  is not built and names the route through `GK.Gui` and the cheat.
- Every coverage record made through the scripted actions was made without rolls or costs, and is
  to be re-read in that light.
- A fair MCP client is not kept from cheats: `GATEKIT_FAIR` filters what it may know and nothing
  else, and `bg3_lua` and `bg3_call` run any verb. To build: a fair client is refused every verb
  of class cheat, by the same registry. A command that prints the verbs with their class does not
  exist; `bg3_api` lists them.
- Not enforced inside a fair step: raw `Osi.*` and `Ext.*` calls, and code the step defers to a
  timer or a tick.
- Judgement calls in the registry of classes, to confirm: the sound setters and the camera as
  fair; `GK.Gui.Resolve` as fair though it steps a window through its view model; switching event
  families on and off as observation.
- Not proven live: that the fair attack spends its action; a miss on the fair path; lockpick as
  fair.
- The acting `GK.Gui` verbs other than `NewGame` and `SaveAs` answer what they attempted, not what
  they saw.

### Rolls and speed

- Inspiration in a roll, for the real run on rolls after the present work. The rule to test: in
  any roll that blocks - a dialogue, a lock, a trap - a point of inspiration re-rolls any die,
  and each attempt spends one point. One test, with a case for each kind of blocking roll: the
  re-roll is offered while a point is held, it draws a new die, it spends exactly one point, it
  is not offered at zero; a pin holds over a re-roll or does not, said either way.
  What is known before the study, from the wiki and the owner's play, each to be checked:
  inspiration is one pool for the whole party, not a resource of a character; it holds four
  points at most, and a point earned above four is lost; a re-roll by any member spends one
  point of that pool; it is earned when a character does something that fits its background;
  creatures outside the party most likely have no such thing. So the cheat is "set the party's
  inspiration", tracked by the ledger, refusing a number above the game's own ceiling unless
  asked to go past it. Checked live since: the pool is held by the party's own entity, six
  points given left four, and `GK.Rolls` reads and writes it there. How inspiration
  is earned in play is not part of covering rolls.

- The roll streams: a stream's seed and count written back to the component give the same dice
  again, a load reseeds them, Karmic Dice overrides a pin and is switched off by it - each was
  shown by a test of the first tree (`research/studies/rolls.md`). Not reached: the streams of a
  fight rewound, and choosing a die instead of repeating one.
- Not verified: critical and multi-die damage; a pin against Karmic Dice on an attack and on a
  target's save.
- `GK.Stage.Clean` puts the speed back to the baseline's, and the contract does not say so.
- The speed measurements were taken with other copies running; they are to be repeated on a
  quiet machine. Not verified at speed: a projectile spell, dialogue and sound, the window in
  front.

### The real coverage of rolls: not yet, and why

What the stage of rolls reached is in `research/studies/rolls.md` (sections 8-18) and proved by
`tests/rolls`. What it did not reach:

- **A death saving throw was not staged.** In the test level every way tried of downing a party
  member ended the game at once (the only party member down) or killed the creature outright
  (a spawned or recruited one dies instead of being downed). It needs a save with a real
  companion in the party.
- **The roller of an initiative roll is not in the roll event.** The function that holds the
  combatant (`esv::combat::System::NewInitiativeRoll`) takes arguments on the stack whose count
  is not read; wrapping it blind was not risked. The row has the roll and no `who`.
- **Which interrupt changed a roll is not in `RollAdjusted`.** It is not among the arguments of
  the function hooked; the game's own log reads it from a component of the same tick
  (`esv::interrupt::UsedOneFrameComponent`). Not read yet.
- **A change after the throw is not tied to the row of its throw by an id.** The roll event has
  no id of the roll; the change carries the condition roll's. A reader pairs them by order.
- **A reroll by an interrupt (Lucky) throws no roll event**: only the change is seen, not the
  new dice.
- **A heal's roll has the type `Sentinel`** and initiative `None`: neither can be named by kind
  in a pin or a rig.
- **The second executable (`bg3.exe`, Vulkan) was run once**: every hook of the fork was found
  in it, and the tests of the roll log's origins, of the game's chance and of a roll changed
  after the throw pass on it. The rest of the tests were run on `bg3_dx11.exe` only.
- **The pin was judged and not changed.** Kept as the standing rule on stock Script Extender;
  the condition in the game's boost words (`when`) is proposed and not built; one throw is the
  rig's business, on the fork.
- **A test's header does not say which Script Extender it needs.** A verb now does (`needs` in
  the table of verbs, one refusal in one sentence: `GK.Verbs.Has`, `GK.Verbs.Lacks`); a test asks
  with `need(...)` by hand. A `needs` in the header, read by the runner, is proposed and not built.
- **The cause of a roll asked by the story or by a dialogue** is the request's own record
  (`request`): the parties and the spell are empty, by the game's own account.
- **The rolls of no dice** seen after nearly every cast are the duration of a status that a
  weapon attack's own properties give a creature that is no player (an AI helper): named.
- **Karmic Dice on damage** was watched once: the dice were not the generator's own. The rule
  of that draw is read in the code and not confirmed by a test.
- **The game's own chance is built for a spell's roll** (`Ext.Stats.GetSpellRollChance`,
  `GK.Rolls.Chance`; section 22). Not built: the damage a hit would do to a given target - the
  server has no cheap function for it; a check that is no spell's roll (a skill check's chance).
- **The build table of `bg3.exe` is refreshed by hand** after a `registry commit`
  (`registry reanchor --target bg3.exe`); a data file of the mod whose places went stale is then
  refused by `registry check` until `registry places` is run on it.

Found on the way, about tests and tools:

- `game.checkpoint()` in a test overwrites the copy's own checkpoint; a test that saves and loads
  needs a named save of its own. `game.reset()` does not open the stage scope again, does not
  put the run's speed back, and its loading screen counts as a surprise.
- The family `dice` asked for ten hours was switched off after about forty minutes.
- The roll log's writing is under `pcall`: an error in it would be silent.
- A probe's snapshot is at most 64 bytes behind one pointer; a rule "this pointer, this offset,
  read there" is wanted. No Lua call turns the index of a game string into its text.
- Windows that block and are not handled by themselves (candidates for the attention system):
  the tutorial "Attack of Opportunity" (closed by `GK.Gui.Resolve{ tutorial = 'close' }`; no
  row in the server's attention log); the reaction window of an interrupt that asks (it held
  `GK.Act.Attack` for 50 s; closed by `GK.Gui.Resolve{ reaction = ... }`); the game-over
  screen (closed only by a reset; `GK.Stage.Clean` gives the hit points back and leaves it).

### Tests and the event log

- Not checked for the guide: whether the limits of `game.server`/`ui`/`lua` count ticks or real
  time; that the guide's example runs as written (it is cut from a passing test).

- Tests in parallel by several scenes in one copy: needs independent scopes of the ledger, each
  with its own Clean and Verify, and rows of the event log tagged with their scene. Several copies
  already run tests in parallel.
- A vocabulary of assertions shared by all tests: an event expected, a state expected, an event
  that must not come, each with the source of the expectation. The prelude's `expect` covers
  components only.
- Waiting until nobody moves, casts or rolls, as one verb on the event log.
- The wait that settles the weapon after a fight, to be replaced by the game's own event.
- The event log: server Lua memory grew over a hundred staged-and-cleaned cycles and was not seen
  to level off; a level-up has no attention kind; the surface family gave no rows; the client's
  rows carry no scope; the drop path and fair mode are proven by unit tests only.
- `tests/_selftest/aim_and_observe.lua` passes alone and fails after `fixtures_and_attention`: the click
  on the spell is not taken and the aim is lost, by something the earlier test leaves that
  Verify does not see.
- `tests/_selftest/clean/attack_in_fight` failed once in many runs with "no character of this
  client is controlled" at its first aim.
- An active roll's opening row has once come without its modifiers: the client read the window
  before it was filled.
- The reset ladder: when a world is not pending it still waits four real seconds; under
  `--attach` only the fixture rung is available unless a checkpoint of the level exists.
- The preview of an area's targets left out an ally inside the blast, which the spell then
  killed: `AimBest` trusts the preview and should check distance against the shape.
- A `GK.Stage.Boost` that tracks itself, so that no test adds a boost by a raw call.
- `GK.Act.Draw` and `Sheathe` go by a server call; the player's own request exists as a
  component (`ServerUnsheathUserRequest`) and is the fair path.
- The relation tables Clean restores are known by placeholder names; they are to be named from
  the game's code.
- Not shown live in Clean: restoring a spell slot or another rest resource, a cooldown, the
  strays of a previous Lua state.
- Clean does not cover a character's own arrangement of things, and does not have to yet - the
  fundamental tests rarely need it. First of these: the weapon set selected (melee or ranged).
  After it: equipment changed, a wizard's prepared spells, icons of the hotbar moved, added or
  removed, and the like. Not settled for the hotbar: its arrangement may affect nothing at all,
  and a test that is about the hotbar could bring it to the shape it needs by a step of its own
  at its start, with no part for Clean.
- How a modder finds the tests: a search by mechanic over the tree's `about` and findings, and a
  page of the map site built from them.

### Studies

- Simple actions that take time - drawing and sheathing a weapon and the like: how the game
  carries each (a state with a duration, an animation the logic waits for, or an instant change
  with the animation for show), what a test waits on, and what "done" is. The weapon is sometimes
  put away with its animation and sometimes moves to the back at once; the first hypothesis is a
  new toggle arriving while the previous one is still carried out, to be tried with any action
  begun meanwhile (walking, aiming a jump, a cast, a dialogue) so that the rule is about
  interrupting the action in general.
- More than one player in one game. Fair actions go through the one interface (one selected
  character, one cursor, one roll window), so two cannot run at once. Ways round it: a second
  local player (split screen, a second virtual controller), a second copy on the same machine as a
  network player, and a network player faked inside one client - one the game takes for real
  while no second copy of the game stands behind it. It opens parallel fair actions, tests that
  need two players, and an agent playing beside a person, and it separates the client logs by
  itself; it goes before the independent scopes above.
- Cases of one test run at once, each by a player of its own. The cases of a test are
  independent by definition, so they are what can be run side by side: with faked network
  players, each case is given a player, an actor and a place of its own in the level, and the
  test takes the time of its longest case and not of all of them. It stands on the two items
  above - cases, and more than one player in one game - and on the gamepad study as its first
  step. To settle when it is built: what the cases still share (the level, the game's speed, one
  server and its ledger), how a case's scene is kept from another's (distance, its own scope),
  and that a failure in one case is not caused by its neighbours.
- What a neutral creature does when it is attacked.
- A sandbox that looks like a test level: a floor with a measured grid, marker objects of known
  size, plain surfaces - a level of our own made with the game's toolkit, or the empty level
  dressed by a fixture.
- The default cast, when the roster is built: staged characters dressed, varied and plain, with
  no abilities or behaviour of their own unless the test asks, women by default; one setting of
  the roster, overridden by any test that names its cast.
- A short film of a test: `test --record` writes a small video of the run at working speed, keyed
  by the test's id and content hash, published in a repository of its own and never committed
  here; made automatically for a failing test. Needs a camera framed by the fixture and capture
  with the window behind others.

### Instruments and crashes

- Surviving a crash (see Decided): none of it is built. The watchdog and the dump are wanted
  before unattended runs.
- The game crashed once in the `memory` test, on the first run after a start, with no cause in any
  log; four runs after a fresh start passed. Not explained.
- The memory instrument: a scan by value; watches of other widths and on reads; a field path below
  the top level; the value before a write. The settings windows as ground to try it on.
- The tracer (`GK.Trace`): a change gives its flags only as `FlagMask`, the names are decoded
  from it. Not verified: its cost per frame, the cheapest noise filter.
- A code pointer's fingerprint across a real patch of the game.

### The toolkit

- `bridge-reload` in the main menu waits for a server side that does not exist there.
- The bridge still serves its former channel, `cmd_<side>.json`; the host does not use it.
- A helper for Osiris calls that names the overloads that exist when the arity is wrong.
- `SweepTests.A_run_records_every_step_lights_types_skips_a_hang_and_resumes` fails under load.
- The verifier: a quick pass overwrites a full verdict; small files hash slowly; the per-chunk
  hashes are unused; the reference list is read whole to be matched; there is no MCP tool; there
  are no rules for `bin/dxdiag.xml` and `bin/db/settings.dat`, so an installed game that has them
  stays "extras" and `doctor` warns.
- GOG installs: no local source of invariants; whether executables and packages are the same files
  across stores is not established.
- `GK.UICatalog` (the game's widgets and what their controls do, from the game's XAML) has no
  command that generates it from the user's own install.
- The offline databases are not served as MCP tools; `gatekit data query` reaches them from the
  command line.
- The agent tools of the first toolkit's server are not ported; `GK.See` and `GK.Act` are reached
  through `bg3_call`.
- Larian's editor (the Toolkit) has no bridge: starting it with a project and a level, calling its
  own objects, switching between editing and playing; the `bg3_toolkit_*` tools do not exist. How
  it could be done: [studies/toolkit.md](studies/toolkit.md).
- Linux: nothing has driven a real game under Proton; WSL2 cannot; it needs an install on hardware.
- On the Vulkan executable `gatekit shot` falls back to photographing the window: the fork's Vulkan
  backend has no capture. The game's own `ls::RenderFrameSystem::Screenshot` would cover both
  renderers. Frame sharing and the deck's other open points: [docs/design/deck.md](../docs/design/deck.md), "Open".
- A deck killed outright leaves a mirrored copy's window where it put it; with
  `GATEKIT_DECK_CONCEAL=hidden` the window cannot be found again.
- The deck: no tool sets a tile's label from what the copy is doing; `gatekit window list` can
  answer "no tile" for a copy straight after a test run ends; no verb reads a copy's frame cap or
  mute, so that only the tile in view is heard and a background tile draws at the lower rate is
  not measured.
- `GK.UI` and `GK.Focus` are not renamed or split (the couplings of
  [docs/contracts/lua-api.md](../docs/contracts/lua-api.md), section 5).
- Weave: hot reload keeps a window's old size (a changed height needs `Weave.Rescue()`); background
  and frame colours draw brighter than written, and the showcase compensates by hand where the
  framework should.
- No CI rule refuses a new script in another language than C# or Lua.
- The screenshots of [docs/curiosities.md](../docs/curiosities.md) are to be retaken, with a
  decision about game imagery.

### The coverage record and its tools

- Not in the toolkit: the collector that writes `research/coverage/coverage.json` from the game's
  registered types; promoting a layout from hypothesis to confirmed after a raw check; holding
  back the layouts the compiler rejects and rebuilding until it passes; renaming fields in the
  extender author's headers to the game's names.
- The query the stand needs most, the wiki joined to the data index: for a functor or a spell type,
  every stats entry that uses it and which of those a player can cast, at what level; `wiki card`
  showing the index entry (and saying when the index does not know an identifier), `data stat`
  naming the wiki page. What it is for: [coverage/mechanics.md](coverage/mechanics.md).
- Refreshed pseudo-code names a field only where the decompiler knows the type. Still raw: a
  component fetched by code the compiler inlined, through a has, remove or unknown helper, through
  a view or a query iterator, or of a type with no layout; the `unknown` helpers are named, not
  typed. Typing the element types of views and iterators is the next step.
- The helper classifier is weaker on `bg3.exe` than on `bg3_dx11.exe`, because fewer of the engine
  functions its rules read are named there; naming them (they are matched in `bg3_dx11.exe`) would
  even it out. A check of `has` per function against the macOS build needs its helpers paired with
  the Windows ones.
- Command-buffer adds and removes (`EntityCommandBuffer::AccessAddStorage<T>`, the deferred
  removes) are not a family, so `symbols users` and `symbols creators` miss a component added only
  that way.
- The type-index globals of the Toolkit DLLs are unnamed and have no `ecs_ids` rows, so the helper
  classifier cannot run there.
- `gatekit symbols digest` lists no text constants for most systems (the references are keyed by
  function starts an update's parts do not always match; FixedStrings are known for `bg3.exe`
  only) and reads no numeric constants.
- A name the symbol database loses stays in the pseudo-code until a full `gatekit decomp run`.
- `gatekit symbols build` has no quick rebuild of the Script Extender part alone.
- The native Linux build of the game, with names of globals and full relocations, is not used as
  anchors yet. Names from the macOS build are not transferred reliably; pseudo-code, call graph
  and ECS type sets together are the next try.
- The systems with request queues (spell casts, statuses, drop and pick up, stacks, move to target,
  rests, cooldowns, relations, passives, tadpole powers, approval): the push found in the code, the
  layout checked live by raw memory and the factory size, one request tried live. Every author
  system is to be checked against its factory's allocation size.
- The leads that are recipes not yet replayable: [coverage/leads.md](coverage/leads.md).
- Unsheath timers read in code and not measured: summoned 3.0 s, coating 2.35, combining 1.5,
  lock picking 1.5. To confirm: capture `esv::unsheath::*Component` while a summon appears, a
  weapon is coated, items are combined, a lock is picked; then they may go to the curiosities.
- Six `ARM_Countercharm_Instrument_*` stats entries use otherwise unused instrument types; whether
  a player can reach them is to be found in the templates and treasure tables.

### The Script Extender fork

Waiting for the owner's decision:

- An element engine of our own (`Ext.UI2`): [se-fork/weave-native.md](se-fork/weave-native.md).
- Network access for mods, gated: a security risk, not acceptable upstream; only on the owner's
  explicit word.
- The keyboard and controller interface switch (`ecl::EocClient::SetControllerMode` /
  `SetKeyboardMode`, located by pattern and called on the client thread): a wrong pattern or
  context crashes and it breaks first on a patch; only on the owner's explicit yes.

Layouts and names of the extender's author, each to be checked live before it changes:

- Declared as tags, but the constructor stores a byte: `eoc::CanTriggerRandomCastsComponent`,
  `eoc::PickingStateComponent` (confirmed: 255 on the server's characters; the game's name of the
  byte is not known), `eoc::item::DestroyingComponent`, `eoc::lock::AnimationStateComponent`,
  `eoc::rest::LongRestInScriptPhase`, `eoc::rest::ShortRestComponent`,
  `esv::IsMarkedForDeletionComponent`, `esv::ScriptPropertyCanBePickpocketedComponent`,
  `esv::ScriptPropertyIsDroppedOnDeathComponent`, `esv::ScriptPropertyIsTradableComponent`.
- Declared with a one-byte field, but the constructor writes nothing:
  `eoc::GlobalLongRestDisabledComponent`, `eoc::GlobalShortRestDisabledComponent`,
  `eoc::StoryShortRestDisabledComponent`, `eoc::interrupt::ConditionallyDisabledComponent`,
  `eoc::interrupt::PreparedComponent`, `eoc::interrupt::ZoneSourceComponent`,
  `eoc::photo_mode::DummyShowSplatterComponent`, `esv::surface::LevelLoadedComponent`,
  `ls::SoundRoomCurrentStateComponent`.
- The game's name as primary, with the present name as a second alias (`P_RENAMED` takes one old
  name; a second needs a small extension): `eoc::DifficultyCheckComponent.AbilityDC`
  (`SpellDifficultyCheckByAbility`), `eoc::progression::MetaComponent.ClassLevel`
  (`ProgressionOverrides`; the types differ, check the layout first),
  `eoc::sight::EntityViewshedComponent.Viewshed` (`VisibleEntityGuids`).
- `CombatCameraParams`: the use in `bg3.exe+0x1F21DB0` suggests the last three floats are
  ObstructionBufferTime, MinChangeCameraTime, MinAngle. `TLLookAtAutomationConfig::EyeLookAtBone`
  is declared `uint8_t` where the code copies a FixedString.
- `DeathData.InstantKill` as `AllowDownedOnDeath`: needs a party of several, where one member's
  death goes to downed. `GameTime.field_C` is the unscaled frame delta, to be named (the game's
  own name was not found).
- From the code, not checked live: `RollAdjustments.FixedRollBonuses` holds 20-byte
  `eoc::interrupt::RollAdjustmentMetadata`, not 24-byte `FixedRollBonus`; the client's
  `CameraBlockerTrigger` holds only an entity after the trigger base; `CastStartRequest` carries
  one more Guid than the game's `StartSpellCastRequestOneFrameComponent`; `UGCModDependency` is
  0x50 bytes (a u64 and three STDStrings); `ExternalCommunityUserID` is 24 bytes (a kind, a byte
  and a payload of 8 or 16); `ScreenFadeCreationData` +0x14 and `ClearScreenFadeData.FadeSpeed`
  hold a float duration (no fade caught live yet); `TLPreviewDummy` +0x88 is the Toolkit's
  `SourceObject`; `SpellUnprepareInfo.field_20` lies at +0x30; the members of `ScratchString` are
  in doubt in order or name.
- UI commands with a string parameter cannot be executed from Lua, and properties set by a binding
  read as nil; a native `ExecuteCommand` crashed the game and was removed.

Not built in the fork:

- Timeline event keys: `TLKeyBaseComponent::Keys` is hidden while the key structs are declared;
  each key layout is to be checked live, then shown.
- On Vulkan: HDR colours (the conversion exists for DX11 only) and image files
  (`RenderingBackend::CreateTexture`; Weave enables images only on DX11).
- `.dds` image files: only png, jpg, tga and bmp are decoded.
- Dragging a window by its title bar only (`window.MoveFromTitleBarOnly`); docking on the 1.93
  base, not looked at.
- Not verified: how the HDR result looks on an HDR screen (the default paper white of 200 nits,
  `Ext.IMGUI.SetPaperWhite`, is a guess); mouse clicks injected into the game's own interface in the
  background.
- The commit "IMGUI: decorations, canvas, element geometry, removable styles" is to be split into
  four before it is offered upstream.
- A coverage batch rebuilds slowly because `Components/Coverage.h` is included almost everywhere;
  the new layouts belong in a translation unit of their own.
- The fork's worktree takes its externals through links into the root checkout's `External`.
- The bridge and the console do not use all of the fork: Osiris name completion from
  `Ext.Osiris.GetFunctions()`, messages from `Ext.Events.ConsoleMessage`, `OnHistory`,
  `OnCompletion` and `CursorPos` for the input line, `SetClipboardText`, `LastScroll`, real time
  stamps.

### Found by the proof of concept
What concerns shared code; each with its number in `research/proof-of-concept/decisions.md`. What
concerns one mechanic is in that executor's chronicle (`research/proof-of-concept/reports/`).

The fork:
- Mended in the instruments build: a watch on reads, a hit's value before the write, the
  tracer's entity and filters (95).
- Wishes: the natural die of an attack, once found; W-0126, the client's pointer.

The memory instrument and the code's names:
- `GK.Memory` is the server's only; `Field` stops at an array (use `Element`); no verb reaches an
  AI-grid tile (95).
- `decomp refresh` gives the project only the registry's applied set, and every function of the
  registry is on a shown rung, so none of their names or ties reaches the project until one is
  confirmed; `decomp grep` does not find a name the registry gives. Enumerations and signatures
  are not applied by any script yet.
- The layouts hold no nested struct; `structs.jsonl` holds such fields meanwhile (95).
- `symbols game` and `decomp matches` pair none of the Toolkit's named roll functions with the
  game (95).
- Wishes from the descent: `GK.Memory.Tile(position)` (the walk from a bound to its AI-grid
  tile); `Field` for a structure that is no component's; `GK.Memory` on the client; a way to read
  a function's return value; a return chain deeper than six; the move of a watched field stamped
  by the fork at the hit; a watch re-polled after a load (101, 103, 107).
- `decomp code`'s field substitution missed one access it should have written
  (`*(longlong *)(manager + 0x18)`); a variable the decompiler reuses cannot carry one name (101).
- Three declarations of Script Extender suspected wrong: `RequestedRollComponent.SpellCheck` is
  the roll's verdict, a stream's `Frame` is its id, the manager's `LastFrame` is the last id given;
  the tracer's `Entity` is never filled and its `Flags` cannot be read (102).
- A bridge reload in the middle of an open stage scope loses the scope (103).

The stage:
- `RestoreMember` restores spells before equipment and leaves a second copy of the weapon's
  attack, which stops the game granting the unarmed strike; Verify compares spells by name (96).
- Verify calls `esv::unsheath::DefaultComponent field_0 = 3` dirty after a toggle test; it does
  not see a level object's state (a door left open), a weapon set, the hotbar, client state (96, 41, 19).
- `GK.Stage.Outfit` is not undone by the clean; `GK.Stage.Fight` starts no fight with an enemy
  already immortal (61, 76).
- **For the next proof of concept, by the owner's wish:** a cheat verb of the stage that gives a
  creature its action, bonus action, movement and reaction back inside a turn, through the
  ledger, so that a test with several attacks neither ends its turn nor waits out the turns of
  enemies that are to do nothing; then the combat tests rewritten on it. Writing the resources'
  amounts on the server works (one aiming test does it); `Osi.RestoreParty` gives no movement
  back inside a fight (76; the summary, "For the next proof of concept").
- A copy's profile shows the game's first-time tutorials (24, 53).
- No test of strays through two bridge reloads (23).
- `GK.See.Around` lists the sandbox's waiting hirelings as neutral characters near the host.

The stage, further:
- `GK.Stage.Spawn` and `GK.Cheat.Spawn` take no rotation: a door placed for a test lay along the
  line it was meant to cross, and a conclusion about doors was drawn from it and withdrawn (104).

Aiming:
- A fair `GK.Gui.AimBest` is not built; `GK.See.Perception` is. **By the owner's amendment** it
  leaves the unseen out of the line of the aim as well as out of the area: it neither counts nor
  avoids an unseen creature on the flight line, so an aim it chose may be stopped by an enemy it
  could not count, for a single target and an area alike (the summary, "For the next proof of
  concept").

The documents:
- **By the owner's wish:** the document on writing tests gains a step - a test finds the longest
  waits inside itself that its rule does not need and removes them (the dialogue tests wait for
  the first line to end before the roll); the runner names the slowest steps and the time spent
  in waits (the summary, "For the next proof of concept").

The runner and the event log:
- The ladder polls Verify every 250 ms; the design that replaces it: Clean names the rows it
  awaits and the ladder waits on them (56).
- No new baseline after a test's `game.reset()` (85).
- The host's interpreter garbles a text pattern over Cyrillic text (47).
- `gatekit test tests` in a copy just started once answered that the level was not read (6).
- No `game_turn` row with `who` after a fight began in `TestLevel_Tom`, once (76).
- `tests/_selftest/rolls_pin.lua` overlaps the pin tests to be written anew and is named by a unit test (12).

The client's verbs:
- Lua on the render thread (`Ext.UI.Defer`) reads the ECS while the main thread changes it: the
  leading explanation of "no character of this client is controlled"; the reads of Aim, Aiming,
  Confirm and UseSlot stay there, with a retry (29, 38).
- `Aim` waits a settle on the real clock (60); the fair verbs' guard counts client ticks.
- `GK.Gui.Resolve{dialogue='last'}` can choose an answer while the one just given has not taken;
  `GK.Gui.Click` does not fall back to a control's own command (47, 96).
- The weapon set's key is not always taken; an orphaned preview cast can stay on the server (65, 96).
- `GK.Act.Press` sits in `GK.Act` and may belong under `GK.Gui` (57).

The copies:
- A game closed with no cause in any log three more times; a stall of one tick in seven seconds;
  the deck closed with nobody known to have closed it (62, 83).

