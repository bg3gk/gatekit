# Writing a test

What to read before writing a test of the game: what a test is and how it is addressed, what makes
its verdict trustworthy, how the file is written and run, and the method by which a mechanic is
studied before its test exists. The API a test calls is held to [api-rules.md](api-rules.md); the
command that runs tests is `gatekit test` ([contracts/cli.md](contracts/cli.md), `test`); the
verbs inside the game are in [contracts/lua-api.md](contracts/lua-api.md).

## The first rule

No test for the sake of a test. A test exists because it answers a question about the game: it
states the rule it checks, it could fail, and a reader learns from it how the game works. A test
that repeats another with other numbers, one that cannot fail, and one whose verdict nobody would
act on are not written. The work is measured by what is understood - rules stated, mechanisms
explained, code named - never by the number of tests.

## Words

| Word | Meaning |
|---|---|
| **test** | a Lua file under `tests/`, or a folder there that holds `test.lua`, that runs against a real game; not a **unit test** ([AGENTS.md](../AGENTS.md), "Where things live") |
| **suite** | a folder of tests; its `_suite.json` (schema `tests/suite`) says in `about` what the folder answers for |
| **case** | a variation of a test's one rule, inside the file (`case`, below) |
| **step** | a named part of a test or a case, of class `fair`, `cheat` or `observation` ([api-rules.md](api-rules.md#fair-cheat-observation)) |
| **venue** | where a test runs: a level started fresh, a save, or the main menu, declared in the header |
| **sandbox** | the empty level `GustavX:Basic_Level_A`, the preferred venue; other small levels are in [facts.md](facts.md) |
| **fixture** | a prepared scene built in one call next to the party from the game's own data - a fight that really starts, a talker, a trader, loot, a lock, a trap, a downed member, a message box. The namespace is `GK.Fixture` ([contracts/lua-api.md](contracts/lua-api.md#gkfixture-scenes-staged-in-the-empty-test-level)) |
| **cast**, **actor** | the creatures a test stages, and the one that acts - by default the host's character |
| **batch** | the tests that declare the same venue and mods, run in one game one after another |
| **reset rung** | one way of making the world clean between tests (the reset ladder, below); not the registry's rung of certainty |

Words not used: **scenario** - in commands, help, documents or code, a test is a test
(`GateKit.Testing`, `GameTest`, `test { ... }`); **kit** for a fixture - the namespace is
`GK.Fixture`. The word for a game copy is in [concepts.md](concepts.md), "Copies"; the word for a
class of verb in [api-rules.md](api-rules.md#fair-cheat-observation).

## Addresses

A test's address is its path under `tests/` without the extension; a case follows it, then a
step, joined by `/`: `combat/melee/attack_by_die`, then `combat/melee/attack_by_die/die 20`, then
`combat/melee/attack_by_die/die 20/attack the goblin`. The folders nest by the game's own notions
(`rolls/`, `combat/weapon/`); a file is one test; its cases live inside it. A test that keeps files
beside it is a folder (Files beside a test, below), and its address is the folder's path:
`startup/videos/own_clips` for `tests/startup/videos/own_clips/test.lua`. The toolkit's own checks
that need a game stand apart under `tests/_selftest/`. A folder and a test of the same name do not
stand side by side, and a case is not named like a step of its test, so that an address reads one
way only.

In running text - a leading comment, a study, a finding, a document - an address carries the prefix
of its kind: `test:` before a test's address, `code:` before a registry id of the game's code,
`event:` before a kind of the event log. A verb (`GK.`), a command (`gatekit `), an MCP tool
(`bg3_`) and a file path need none. A file or a document is addressed by its path relative to the
repository's root, as a link where the format has links. In a field whose kind its schema fixes (a registry entry's
evidence, a header) the bare address is written. The forms and the rule for code:
[registry/README.md](../registry/README.md), "Citing code and tests". An address here is the
written form of a test, an id or a kind; a machine address of the game's code
(`bg3_dx11.exe@25605617+0x1BC3C2B`) is another thing, and the same section of the README says
where one may stand.

`gatekit refs <address>` lists every place that cites a test or a registry id. `gatekit registry
check` refuses a `test:` citation that names no test under `tests/` and a `code:` citation that
names no entry; the case and step after a test's path are not resolved. A renamed entry
(`gatekit registry rename`) leaves no alias: every citation is rewritten and the old id exists
nowhere.

## What a test is

**About one thing.** Variations of that one thing belong together as cases of one test: shooting a
ranged weapon may run over every kind of ranged weapon, each kind a case reported by its name. A
different thing is a different test even when it looks alike: a ranged spell is not a ranged
weapon. The line is the rule being checked - the same rule with other values is a case, another
rule is another test.

**A worked example.** A stranger runs it with one command on a clean game with GateKit and learns
from it a minimal scene, the action, what must happen and why. It holds nothing it does not need,
rather than being short: a dialogue with three rolls is a proper test when every step earns its
place. Its cast is the plainest the rule allows. The target of an attack is hostile
(`faction = "enemy"`): a neutral creature answers an attack with a dialogue, which drags in the
story, relations and crime. The test says so as a precondition (`need`), so that a scene staged
wrong ends as unmet and not as a strange failure.

**Composite.** A test that stands on simpler ones - a fight already begun, a dialogue already open
- names them in its leading comment by their `test:` addresses, so that a failure says whether the
new part broke or the base did. A simple test takes at most ten seconds; a composite states its own
limit in its leading comment, with the reason. Nothing enforces either; the run's times show them.

**Once is enough.** A test does what it does once. Its verdict is a rule, and a rule shown once is
shown; a pinned roll removes the reason to try again. Doing the same thing a second time needs a
strong reason written in the test: the thing tested is the repetition (a scene staged and cleaned
again and again - then a handful of times, five and not a hundred), or the rule is about what
changes between two runs. Variations of one thing are not repetition: they are different cases.
A long run is a measurement, kept apart, asked for, and named as one
(`gatekit test --repeat <n>`). A test that has to be repeated to pass is a defect, in the test or in
the API, never a setting.

## What a verdict needs

Hard is what guards against a false verdict: the class of every step, a clean scene, a decided
roll, the scene's geometry stated, and for an attack a hostile target that answers with no
dialogue. Flexible is how a test describes its scene and its preconditions: `need` takes any
condition, no list of preconditions is required, and a shared helper is made only when the same
condition has been written by hand in a third test.

**Rolls are decided.** A test does not wait on luck. `GK.Rolls.Pin{ who, kind, n, once }` fixes
the natural die of a creature's own rolls of one kind; the modifiers, the difficulty and the game's
verdict stay the game's. Every pin is a row of the stage ledger, so the clean takes it off. Karmic
Dice overrides a pin on the rolls it weights, so `Pin` switches it off for the scope
(`karmic = true` keeps it on); a test that needs plain unpinned dice calls
`GK.Rolls.SetKarmic(false)`. Initiative is pinned as a bonus, not a die. The kinds, `once` and when
a pin binds: [contracts/lua-api.md](contracts/lua-api.md#gkrolls-rolls-decided-in-advance-serverrollslua).

**Geometry and state.** A claim about what blocks or lets through - a wall, a door, an object, a
creature on the line - is made only with the geometry and the state it was seen in, as preconditions
of the test: where the thing stood and how it was turned, open or closed. Without them a result is
not a finding.

**The same verdict at both speeds** (Speed, below).

## Time

A step moves on when the row it waits for arrives in the event log, never after a pause of a fixed
length; a limit is only a guard against a hang. A step that waits for time, polls or retries is a
defect unless the wait is what the test is about ("a wait is a smell"). The rule, and where an
event comes from when the game seems to give none: [api-rules.md](api-rules.md#events-not-waits).

- **Limits count the game's ticks.** Every limit of a wait of `game` (`wait`, `wait_row`,
  `wait_attention`, `answer`) is seconds of a running game, converted to the server's ticks at the
  nominal rate and counted from the copy's heads and heartbeats: a game that stands still eats
  nothing of it, and one that shows no progress for a minute is given up as hung. The server ticks
  at a fixed rate whatever the speed. The client's ticks are frames, drawn at the renderer's rate,
  so a client's limit counts each frame by its time (at most 100 ms a frame), never the frames
  themselves ([contracts/bridge-protocol.md](contracts/bridge-protocol.md), "Observed answers").
  `game.wait_state` and `game.sleep` count the host's clock.
- **A timing inside a test is given in ticks** where the rule is about ticks: every row carries the
  `tick` it was made in, and `GK.Speed.Clock()` gives the server's tick count. A rule stated in
  ticks holds at any speed; one stated in the host's seconds does not: a weapon drawn on request
  is held to within a few ticks of the request, not to a number of seconds.
- **A test never waits for an animation.** The server's state changes at once; the animation is the
  client's show of it. A step ends on the server's row - `event:weapon_state` for a weapon drawn,
  `event:game_hit` for an attack - not on the model.
- **A test's own sleeps are a defect.** `game.sleep` is a sleep of the host's thread. The prelude's
  `settled`, `alive` and the `within` of `expect` poll with it in the host's seconds.
- **Resources are given back, not waited for.** A test that acts several times in a fight gives the
  actor its turn's resources back between the acts - `GK.Stage.Refill{ who, action, bonus, movement,
  reaction, slots }`, a cheat through the ledger - instead of ending the turn and sitting through
  the enemies' turns. A test whose rule is that an act spends a resource reads the spending before
  it refills.

**A test accounts for its own time.** After every run the runner prints the five slowest steps by
address with the time each spent in the waits of `game` and in the game's own answers, and the time
of the whole run inside each kind of wait (`wait`, `wait_row`, `wait_attention`, `answer`,
`wait_state`, `sleep`); `results.json` holds the same per test and step. Finishing a test includes
reading them, naming its longest waits, and for each saying whether the rule needs it. A wait the
rule does not need is removed - a line of dialogue skipped as a player skips it, a scene staged
closer to the moment under test, a row waited for instead of a state read until it changes,
resources given back instead of a turn ended - or recorded as a gap with the reason it cannot go.

## Speed

A finished test runs at the working speed, `GK.Speed.Working` - one named value, so that it is
raised in one place (its value, and what breaks in the game at high speed:
[contracts/lua-api.md](contracts/lua-api.md#gkspeed-the-working-speed-and-the-games-clock-serverspeedlua)) - and gives the same verdict there as at
speed 1. The speed is the run's: `gatekit test --speed working` and `--speed 1` start every test at
that speed and hold it before each test; without `--speed` a test runs at the copy's speed. A step
that sets the speed itself (`GK.Speed.Set`) changes it until the clean puts the baseline's back.

A test that fails only when fast is first a suspicion against our own API: something waits on the
clock where it should wait on the event log, or reads a state before it has settled. Only when the
cause is shown to be the game's own does the test declare a ceiling, with the finding that proves
it: `speed_ceiling = <n>` with `ceiling_finding = "<where the finding is written>"` in the header.
A ceiling without its finding is refused when the file is read; the runner never starts the test
faster than its ceiling, with or without `--speed`.

## The file

```lua
-- The leading comment: the rule the test checks, its source, how its scene is made, why it
-- matters, and the class of each part (staging: cheat; acting: fair; reading: observation).

test { level = "GustavX:Basic_Level_A", about = "One sentence: the rule." }

function run(game)
    step("a name", "observation", function() ... end)
end
```

The file runs on the host in an embedded Lua 5.2 interpreter (MoonSharp), not in the game; it
reaches the game only through `game`, which sends Lua chunks to the game's own Lua. The host's Lua
has no `//` and no `load`, `loadfile` or `dofile`; `#text` counts characters, `bytes(text)` the
UTF-8 bytes the game counts. A test loads a shared file - one whose name begins with `_` - beside it
with `require("_name")` or in a sibling folder with `require("folder/_name")`; a file whose name
begins with `_` is never run as a test.

**The header**, `test { ... }`, declares:

| Field | Meaning |
|---|---|
| `level`, `save`, `menu = true` | the venue: `MODULE:LEVEL` started fresh, a save loaded, or the main menu |
| `mods` | the mod folders besides the base game; left out, the copy's own |
| `reset` | the reset rung the test needs before it (the reset ladder, below) |
| `about` | the rule in one sentence; left out, the leading comment stands for it. Either is shown with a failure |
| `before` | in a test of cases: the name of the file's function that stages what every case shares |
| `surprises` | the surprises of the event log the test allows (Surprises, below) |
| `speed_ceiling`, `ceiling_finding` | the highest speed and the finding behind it (Speed, above) |
| `files` | files put into the copy's game folder before the test's game starts: `{ ["<place in the game folder>"] = "<file name>" }` (Files beside a test, below) |
| `startup_videos = true` | the game starts with its startup videos played, the game's and the mods' (`up --startup-videos`) |
| `until_lua = true` | the test runs as soon as the bridge first answers, in the client's `LoadModule` state, right after the startup videos (`up --until-lua`); with no level, save or menu |
| `drawn = "why"` | the test needs drawn frames (it counts them, or reads what only a drawn frame lays out); in a headless run (`test --headless`) it is not run and counts as UNMET with this reason. A picture alone needs no `drawn`: `game.shot` draws for it |
| `drawn_start = "why"` | the test needs what only a drawn start sets up (a reaction prompt's window); in a headless run its game's start is drawn until the client's first `Running`, nothing after - a group of its own |
| `logic_diff = { ... }` | [`gatekit logic-check`](contracts/cli.md#cover-cover-fold-cover-diff) compares the test's rendered and headless event records using these rules |
| `first_launch = true` | the game starts as a first launch and the test runs on its first page, `Eula`, the pages left to it (`up --first-launch`); with no level, save, menu or `until_lua`; such a start is never shared with tests that do not declare it |

Every start hides the startup videos, the game's and the mods' (`up`, `--startup-videos` in
[contracts/cli.md](contracts/cli.md#up)): a test that is not about them does nothing for it. A
test about them declares `startup_videos = true`; such a start is its own and is never shared with
tests that do not declare it, nor taken as met by a copy already running. A test of what the
start itself did declares `until_lua = true` too and ends where the start's record is complete:
there is no menu, no session and no server then, the game goes on loading its menu meanwhile, and
the test reads only what the start left (`Ext.StartupVideos`). Such a start is shared only with
tests that declare it the same way.

Any other field is refused when the file is read, naming the field and the fields above
(`the header test { ... } does not know 'clean'; it knows about, level, ...`): a field nobody reads
is never ignored without a word. The list is `GameTest.HeaderFields`.

**`step(name, class, fn)`** runs `fn` as a step of class `fair`, `cheat` or `observation` and
returns what `fn` returns. Every chunk the step sends carries its class, and the game refuses a verb
the class does not allow: a fair step that calls a cheat fails ("a fair step called the cheat
GK.Cheat.Attack"), and so does an observation step that calls anything but an observation, even
when the chunk catches the refusal itself. The host's own `game.*` functions are held to it too.
What a verb calls inside itself, a raw `Osi` or `Ext` call, and code the chunk leaves to a timer are
not judged ([contracts/lua-api.md](contracts/lua-api.md#the-class-of-a-verb-and-steps-sharedverbslua-sharedverbclassesjson), "Steps"). A failure stops the
test and names its step: `step <name> (<class>) failed: <why>`. Steps do not nest. A step's begin
and end are rows of the event log, so every row between them belongs to it.

**`need(condition, why)`** returns `condition` when it holds; otherwise the test ends as **unmet:
why**. Unmet says nothing about the thing tested - only that its precondition does not hold here.

**`case(name, fn)`**, at the file's top level, makes a test of cases. A test has cases or it has
`run(game)`, never both. Each case `fn(game)` runs from a clean scene: between two cases the runner
runs the ladder again with the test's reset rung, and the function the header names in `before` runs
again before each case. Steps run inside a case (a step outside one is refused); a failing step ends
its case, a failing case does not stop the others, and `need` unmet inside a case makes that case
unmet. The test fails if a case failed, is unmet if every case was, and passes otherwise. A case's
name is lower case, has no `/`, is given once and is not the name of a step of the test; cases do
not nest. Cases are reported each with its outcome and time under its test, a step's rows carry its
case, and surprises are listed per case.

**Outcomes.** A test ends **pass**, **fail** or **unmet**; `gatekit test` exits 0 when all passed,
1 when one failed or was not run, 2 when none failed and one was unmet. Each test's output folder
holds `verdict.txt`, and after a failure `failure.jpg`, `console.txt` and, when the game died,
`crash.txt`; the run's folder holds `results.json` (schema `record/test-run`). The lines printed and
the folders: [contracts/cli.md](contracts/cli.md), `test`. What a test makes itself - a picture, a
saved text - goes into its output folder too (`game.out`, where a relative path of `game.save`
and the other file functions lands); a test writes nothing under `tests/`, and a write there is
refused.

**What `game` offers**:

| Function | What it does | Class |
|---|---|---|
| `game.server(code [, timeout])`, `game.client`, `game.ui`, `game.lua(side, code [, timeout])` | run a chunk in the game, return what it returned | the step's |
| `game.wait(side, expression [, timeout [, every]])` | read `return (expression)` until it is truthy | the step's |
| `game.mark()`, `game.events([since [, kinds]])` | the event log as one stream: a cursor now, as each side of the game says it; `{ rows, next, dropped? }` after a cursor | observation |
| `game.wait_row(since, kinds [, match [, seconds]])` | the first row of one of `kinds` after `since` that `match(payload, row)` accepts | observation |
| `game.attention()`, `game.wait_attention(kinds [, timeout [, since]])` | what waits for the player; the first item of one of `kinds` after `since` | observation |
| `game.wait_state(state [, side [, timeout]])`, `game.state([side])` | the bridge's heartbeat | observation |
| `game.dialog()`, `game.answer(n)` | the dialogue on screen; choose answer `n` and wait until it changes | observation; fair |
| `game.key(name [, hold])` | a key pressed through the bridge into the game's input (the window only when the bridge is not there) | fair |
| `game.checkpoint()`, `game.reset()`, `game.reload()`, `game.skip_scenes()`, `game.fast_scenes(speed)`, `game.burst(senders, each)` | a checkpoint and its reload, a bridge reload, scenes skipped or played fast, a load on the channel | cheat |
| `game.shot(path [, width [, overlay]])`, `game.logs{ story, lines, grep }`, `game.windows()`, `game.process()` | a picture (the game's frame; `overlay = true`: with the extender's own layer), what Script Extender printed, the copies in a deck, the game's memory use | observation |
| `game.cover(name)` | the coverage counts since the last segment saved as `cover/<name>.txt` in the test's output folder and started again; compared with `gatekit cover-diff` (docs/contracts/cli.md) | observation |
| `game.now()`, `game.sleep(seconds)` | the host's clock | observation |
| `game.out`, `game.path`, `game.save`, `game.read`, `game.exists`, `game.mkdir`, `game.remove`, `game.glob`, `game.testimage`, `game.pixel(path, x, y)` | files; a relative path is in the test's output folder. `testimage(path, w, h)` writes a png whose red grows to the right and green downwards; `pixel` reads a picture's colour at a point in parts of its size: `{ r, g, b, width, height }` | observation |
| `game.channel`, `game.extender`, `game.weave`, `game.folder`, `game.profile` | the folders of the copy the run uses | |

A `since` left out is `game.mark()` at the call. The kinds a test names to `game.events`,
`game.wait_row` and `game.wait_attention`, and the dialogue's kinds through `game.dialog` and
`game.answer`, are what it asked for (Surprises, below).

The prelude (`src/GateKit.Testing/Prelude.lua`) adds `row(since, kind [, match [, seconds]])` -
`game.wait_row` under a shorter name - `check(condition, message, ...)`, `eq(got, wanted, message)`,
`same`, `near`, `fmt`, `has`, `contains`, `keys`, `values`, `sorted`, `count`, `join` and the
string helpers; `watch(side, component)` with `alive(side, component)` for a component that lives
one frame; and `expect.appears`, `gone`, `equals`, `changes` - claims about a component watched in
the game every tick between `expect.begin(list)` and `expect.finish(list)`.

## Files beside a test

A test that needs files - a video, a picture, a data file the game is to read - keeps them beside
it, the same way in the whole tree:

- **The test is a folder.** A folder that holds `test.lua` is one test, addressed by the folder's
  path. Every other `.lua` in it begins with `_` (shared, required as above, never run); any other
  file is data; nothing below it is a test.
- **Shared files** live in a folder `_files/` at any level of the tree. A name the test gives is
  looked up first in its own folder, then in `_files/` of its folder and of each folder above it up
  to `tests/`, as `require` finds shared Lua.
- **`files` in the header** puts them into the copy:
  `test { files = { ["Data/Video/GK_ONE.bk2"] = "ONE.bk2" } }`. The key is a place relative to the
  copy's game folder, the value a file name found as above; both are checked when the file is read.
  The runner writes them before the game the test runs in starts, so the game finds them from its
  first frame: a file it reads only at its start (a startup video, a mod's `StartupVideos.json`)
  needs nothing more. Tests share a game only when they have the same files; the files stay through
  a restart inside it and are taken out after its last test, whatever the verdicts. A test with
  files is not run in a game already up (`--attach`).
- **A resource is kept as text and written as the game reads it**: a `.lsx` file placed at a key
  ending `.lsf` is written there in LSF, converted by the built-in LSLib (the one `build` packs
  with), so a level's objects or a timeline the game reads only in binary are kept beside the test
  as text: `files = { ["Data/Mods/GK_Menu/Levels/SYS_Menuscreen_Act3_B/Triggers/_merged.lsf"] = "trigger.lsx" }`. What the runner refuses and how it keeps
  count: [contracts/cli.md](contracts/cli.md), `test`.

What is committed beside a test is held to rule 3 of [AGENTS.md](../AGENTS.md): only files of our
own making, never a file of Larian's or one derived from the game's data - a test that needs a game
file names it and reads it from the copy. Each file stays under 1 MiB: git keeps every version of a
binary file in every clone for good, and a test's file is the smallest that shows its rule (a
short, small video, not a clip). `dotnet test` fails on a larger one
(`TestFilesTests.Every_file_beside_a_test_in_the_repository_is_under_the_cap`). Binary kinds are
marked in `.gitattributes`; a new kind is added there with its first file.

## The reset ladder

Before every test the world is made clean by the cheapest reset rung that is provably enough:

| Reset rung | What it does |
|---|---|
| `none` | nothing; the stage scope is opened |
| `fixture` | our own clean-up in the scene: `GK.Stage.Clean`, the client's half, then `GK.Stage.Verify` until clean |
| `venue` | the level's checkpoint loaded in the same process; what lives outside the level put back |
| `game` | the game restarted - the last resort |

Between groups of tests that need different starts the run keeps its game: the next group's save,
or the save made right after its level's first start, is loaded in it by the game's own load. A new
process is started only for what a load cannot give - a test of the start itself (`tests/startup`),
files that go in before a start, startup videos, the main menu, another mod list, a new Script
Extender build, a level never started in the copy - and when the game died
([contracts/cli.md](contracts/cli.md), `test`).

A test declares the reset rung it needs (`reset = "venue"`); undeclared, it is `fixture` in a simple test
level and `venue` elsewhere. The ladder starts there, verifies, and climbs one rung only when a rung
fails or leaves the world dirty; the line before each test says which rung held and why. A test in
the main menu has no scene to clean: only a declared `game` applies to it. `gatekit test --reset
<rung>` raises every test's need; `--attach` runs in a copy already up and leaves it running, where
`game` is never taken. What each reset rung does in detail, what makes a level simple, and what
survives a level load: [contracts/cli.md](contracts/cli.md), `test`, and
[contracts/lua-api.md](contracts/lua-api.md#the-stage-scope-gkstagebegin-clean-verify-track-serverstagelua).

**A test does not clean up by hand.** Everything it stages goes through the stage ledger - `GK.Stage`,
`GK.Fixture`, `GK.Rolls`, or `GK.Stage.Track` for staging of its own - so the clean can undo it. A boost
added outside the ledger, by a raw `Osi.AddBoosts`, is named by `GK.Stage.Verify`, and a test that
leaves one behind cannot pass clean; the same holds for story flags and Osiris facts set by raw
calls instead of `GK.Stage.Flag` and `GK.Stage.Fact`. What the clean took back that the test did not
stage on purpose - a fight, a dialogue, a window - is printed before the next test as its side
effects.

**Never create and delete a creature in one Lua chunk.** An `Osi.CreateAt` and an
`Osi.RequestDeleteTemporary` of the same character in one server tick close the game a moment
later, with no error. `GK.Stage` and the fixtures delete such a one a tick later; a test's own raw
calls do not.

## Surprises

After a test the runner lists what the event log holds that the test did not ask for, one line each
under its steps and in `results.json`:

- `blocking` - a state that waits for the player (a dialogue, a roll window, a tutorial, the
  game-over box, a fight) of a kind the test never named;
- `participant` - a creature in the rows that is neither of the party nor staged through the ledger;
- `unplaced` - a row the host's registry of kinds cannot place.

Blocking and participant surprises are a report and change no outcome. A test without cases whose
rows include unplaced ones fails ("the event log holds rows the host could not place"); in a test
of cases unplaced rows are listed with each case's surprises and change no outcome. A surprise
that belongs is allowed in one line: `surprises = { "combat_started" }` in the header, or the same
list in a `_suite.json`, which holds for every test under that folder; `participants` allows every
creature outside the cast. An unplaced row cannot be allowed.

## The instruments at hand

Each is for finding a cause or a mechanism, not for an ordinary run whose times are read.

- **`GK.Trace`** - the entity tracer, on each side: between `Begin` and `End` it records which
  components a step creates, destroys, replicates or modifies, with the frame and flush of each
  change and, asked, the system that made it. Reach for it to learn what the game does around a
  moment, before guessing which component to read. [contracts/lua-api.md](contracts/lua-api.md#gktrace-the-entity-tracer-as-rows-of-the-event-log-sharedtracelua-both-sides).
- **`gatekit test --trace`** - the tracer through every step of a run on both sides, one trace a
  step, written as `trace/<nn>-[<case>-]<step>.json` in the test's output folder. Reach for it when
  a test fails and the step's changes are the question. [contracts/cli.md](contracts/cli.md),
  `test`.
- **`GK.Memory`** - the memory instrument: a field's address (`Field`), which instruction writes or
  reads it (`Watch`, then `Hits`), what depends on a value held fixed (`Freeze`). Reach for it when
  the question is which function changes a value; `gatekit decomp at <module+0xRVA>` names the
  instruction's function. [contracts/lua-api.md](contracts/lua-api.md#gkmemory-the-memory-instrument-sharedmemorylua).
- **Function probes** - `Ext.Debug.AddProbe(address, { Args, Return, Chain, Snapshots })` and
  `GetProbeHits()`: what a function is given and gives back (down), the chain of its callers up to
  32 frames (up), and the memory behind a pointer argument on entry and on return. Reach for it
  when the question is who calls a function and with what. The fork's `GATEKIT.md`, "Function probes".
- **Coverage** - which of every function of the executable ran, and from where: `gatekit cover
  start` / `cover save FILE --reset` (a segment; `game.cover(name)` in a test), `cover-fold` (named
  against the registry), and above all `cover-diff A1 A2 --all --against B1 B2 --names`: an action
  recorded twice against the same scene left idle recorded twice gives the functions only the action
  runs. `gatekit up --cover-start` covers the start itself, before any Lua, one record per state.
  Reach for it when nothing is known of where a mechanic's code is; a probe or a watch then finds
  the branch. [contracts/cli.md](contracts/cli.md), `cover`.
- **The start's timeline** - `Ext.StartupVideos.Timeline([since])` and `gatekit timeline`: what
  happened while no Lua existed or while it was reset (states, the engine's init steps, files
  opened, the loading screen's rows, fades, interface states, timelines' playback). Reach for it for
  anything of the start or of a load. The fork's `Docs/API.md`, "Startup Videos".
- **`gatekit crashes`** - the crash dumps a copy left, newest first, each with its faulting address
  and the function there. Reach for it when the game closed under a test; a test that fails after a
  death also gets `crash.txt`. [contracts/cli.md](contracts/cli.md), `crashes`.

## Tests in parallel

Two copies may run tests at the same time; inside one copy tests run one after another. A test
reaches only its own copy, through `game`, and writes only into its own output folder. It assumes
nothing of the machine beside it: not the window's focus (`game.key` and the fair verbs go through the bridge),
not that the host's seconds are its own (another copy shares the processor, so a timing is stated
in the game's ticks), not a file or a state outside its copy.

## An example

`test:_selftest/guide/attack_by_die`, the file `tests/_selftest/guide/attack_by_die.lua`: two
cases of one rule, the staging they share in `before`.

```lua
-- A natural 1 misses and a natural 20 is a critical hit, whatever the bonus and the armour class
-- (bg3.wiki, "Dice rolls"). Staging (cheat, `stage`): two hostile goblins, one beside the host
-- and one standing off, so that a kill does not end the fight; the host's initiative pinned so
-- that it acts first; the fight started. Attack (fair): GK.Act.Attack - the hotbar's main-hand
-- attack, the aim, the click, answered when the game has finished it. Reading (observation): the
-- game's own hit row.

test { level = "GustavX:Basic_Level_A", before = "stage", surprises = { "combat_started" },
           about = "A natural 1 misses and a natural 20 is a critical hit." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local host, goblin

function stage(game)
    game.wait_state("Running", "server", 120)
    host = uuid(game.server("return Osi.GetHostCharacter()"))
    local staged = game.mark()
    goblin = uuid(step("the host first, two hostile goblins, the fight", "cheat", function()
        check(game.server("return GK.Rolls.Pin{ kind = 'Initiative', n = 100 }").ok, "the host's initiative is pinned")
        local g = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            local near = GK.Stage.Spawn{ template = "goblin", at = { x + 1.6, y, z }, faction = "enemy" }[1]
            local off = GK.Stage.Spawn{ template = "goblin", at = { x - 6, y, z }, faction = "enemy" }[1]
            return { near, off }
        ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil and Ext.Entity.Get(%q) ~= nil", g[1], g[2]), 10)
        game.server(string.format("return GK.Stage.Fight({ %q, %q })", g[1], g[2]))
        return g[1]
    end))
    step("the scene is as staged", "observation", function()
        need(game.server(string.format("return Osi.IsEnemy(%q, Osi.GetHostCharacter())", goblin)) == 1,
            "the goblin is hostile to the host: attacked, it fights and does not talk")
        local turn = row(staged, "game_turn", function(p) return p.who ~= nil end, 60)
        need(uuid(turn.payload.who) == host, "the host has the first turn")
    end)
end

local function attack(die, outcome)
    return function(game)
        step("the die", "cheat", function()
            local pinned = game.server(string.format("return GK.Rolls.Pin{ who = %q, kind = 'Attack', n = %d }", host, die))
            check(pinned.ok and pinned.inForce, "the attack die is pinned", pinned)
        end)
        local mark = game.mark()
        step("attack the goblin", "fair", function()
            local a = game.ui(string.format("return GK.Act.Attack(%q)", goblin), 120)
            check(a.ok, "the game finished the attack", a)
        end)
        step("the game's outcome", "observation", function()
            local hit = row(mark, "game_hit", function(p) return uuid(p.who) == goblin and uuid(p.by) == host end)
            eq(hit.payload.outcome, outcome, "the game's outcome")
        end)
    end
end

case("die 1", attack(1, "critical miss"))
case("die 20", attack(20, "critical"))
```

The ladder takes everything back between the cases and after the test: the goblins, the fight,
the pins. The fight it starts is a blocking state the test expects, allowed in `surprises`. It
runs with

```bash
gatekit --instance <copy> test tests -k _selftest/guide/attack_by_die --speed working
gatekit --instance <copy> test tests -k "_selftest/guide/attack_by_die/die 20" --speed 1
```

`-k` takes a test's whole address (that test alone), a test's address and a case (that case alone),
or any other text (every test whose address contains it). `gatekit run <file>` starts a copy, runs
one test and closes it again.

## Findings and the bar for a bug

What a folder of tests answers for is the `about` of its `_suite.json`; what a test shows is its
leading comment and its `about`; what a study found is in its study under `research/studies/`. A
finding is one of four kinds, each held to what it must prove: a `todo` proves nothing; a `finding`
(how the game works) names its evidence - a `test:` address, a `code:` id, a wiki page; a
`curiosity` is a finding worth telling players; a `bug` - the game's, Script Extender's or ours -
is `suspected` until it is `confirmed`, and confirmed requires all of:

1. the expected behaviour with its source - the game's own tooltip or data first, then the wiki,
   then the code;
2. a test in the tree that shows the difference and gives the same verdict on two runs;
3. a copy verified clean and bare of mods other than ours (`gatekit game verify`);
4. whose bug it is, with the reason.

Only a confirmed bug of the game or of Script Extender is offered outside.

## The method

No test of a mechanic is written before the mechanic is studied, in this order. What each step
found is written down in the study with its source; "not yet, and why" is a result.

1. **The rule, from bg3.wiki and the wider search.** bg3.wiki is the base and is always read first:
   it states the rule. `gatekit wiki find <words>`, `gatekit wiki rules <title>` (a page as its
   sections, with the wiki's own "needs verifying" marks), `gatekit wiki card <key>` (a spell, status
   or item as fields). The search then goes wider, as part of the same step: the game's patch notes
   and developer posts; community write-ups, modders' documentation and Script Extender's
   documentation; forum threads where players measured or argued a behaviour, and the descriptions
   of videos that demonstrate it. The wiki states a rule; a thread gives a claim to test. Every
   source read is named in the study; no text of it is copied into the repository. Two sources that
   disagree are a finding, and worth a test.
2. **The mods that touch it.** The study mods kept in the home first: `gatekit mod usage <folder or
   .pak>` lists the components, functions, events and stats a mod uses. Then mods made for exactly
   this mechanic, downloaded only with permission, unpacked with `gatekit mod unpack <pak>` and
   scanned the same way. A mod is untrusted data - read, never run, loaded or installed. A study
   without this step is not accepted.
3. **Script Extender**, upstream's current main and the fork: what it already maps for the
   components and systems concerned. `gatekit symbols fields <type>` shows a type's fields from the
   Toolkit's accessors and Script Extender's layout.
4. **The instruments**, before any reading of code. What to watch, change and hold comes from the
   earlier steps, each stated as a hypothesis with its source (the wiki, a player's experience of
   the game, a mod) and afterwards marked as held or not. The tracer names the components a step
   touches; the memory instrument names the instruction that writes a field and what depends on a
   value held fixed (The instruments at hand, above). Only then is the function read and named:
   `gatekit decomp code <id, name or binary+0xRVA>`, `gatekit decomp at <module+0xRVA>`,
   `gatekit symbols digest <system>`. A claim about code that an instrument could have checked and
   did not is not accepted.
5. **The venue**, the sandbox preferred, and the test.
6. **What is left behind**: the tests; the study; the names of the code, in the registry, each on
   its rung of certainty with its evidence - `gatekit registry note <id> --rung <rung> --evidence <kind>:<text>
   --test <path>`, a test's address being evidence that can confirm - committed into the checkout
   with `gatekit registry commit` (the rungs of certainty and the kinds of evidence:
   [registry/README.md](../registry/README.md)); and, where the mapping is certain and useful
   beyond the case, an addition to the fork.

**Asking a person.** Asking is a step of the method, not a failure of it. When the study has been
made and the work still stalls, the work does not go on by guessing: the questions for a person who
knows the game from playing it are written into the study (`research/studies/`), several
together; rows of kind `question` beside the tests are decided, not built (below). Each is concrete and
carries its context - what was read, what was tried and seen, what is expected, and what the answer
decides - so that it can be answered in a line. The work goes on with what does not depend on the
answer. An answer is a hypothesis with its source ("a player's experience of the game") and is
checked like any other; it is recorded in the study with what the check showed.

**Closing without the code.** A case may close without its code - the last resort, after every step
and never instead of them. Its tests and its study stand; the code part is recorded as open with
what was tried, what each attempt showed and where it stopped, and nothing is named on a guess.

**Script Extender itself.** The last step of every case checks Script Extender against what the
case established: the layouts, field types and names, enumerations and signatures it declares for
what the case touched (`gatekit names extender` lists the names that rest on its declarations
alone). An error is held to the bar of a confirmed bug: the expected value with its source (the
machine code at an address, a value read from memory, the game's own data), a test that shows the
difference twice on a clean bare copy, and proof that the fault is in the declaration and not in our
reading. "None found among what was touched" is written down too.

## Decided, not built

So that nobody looks for what is not there:

- Surprises are a report first: whether a blocking or a participant surprise fails a test is
  decided later.
- The speed declarations `independent` (speed has no effect, as in the main menu) and `fixed` (the
  thing measured is time itself) for whole families, and the speeds a test is proven at, written by
  the tool.
- Typed rows beside the tests - `finding`, `bug`, `todo`, `curiosity` and `question` - and
  `gatekit findings` to read them.
- `gatekit study`: one document per mechanic, filled step by step by the method.
- `gatekit test check`: a header, its preconditions and its classes held to a schema; every test,
  case and step cited resolved; no folder beside a test of the same name.
- `gatekit test move`: a test moved with every citation of it rewritten in the same act.
- `event:` citations found by `gatekit refs` and resolved by the checks.
