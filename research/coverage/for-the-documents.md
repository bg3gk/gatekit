# What the documents must say

The owner's decisions that the rewrite of the documents is checked against, each with the
document that owns it. A line is ticked off when its owner states it and no other document
restates it. This file leaves the tree when the rewrite is accepted.

## Words

| Decision | Owner |
|---|---|
| A **test** runs in the game; `tests/` holds them as a tree by domain. The word "scenario" is not used - in commands, help, documents or code (`GateKit.Testing`, `GameTest`, `test{...}`). | `docs/writing-tests.md` |
| A **unit test** checks the toolkit, with no game; `unit/`, projects named `*.UnitTests`. | `AGENTS.md` (checking your work) |
| A **verb** is one function of the API inside the game. Defined once. | `docs/api-rules.md` |
| `fair`, `cheat`, `observation`; never "honest". | `docs/api-rules.md` |
| What can be known: `player`, `omniscient`. | `docs/api-rules.md` |
| Venue, sandbox, fixture (`GK.Fixture`, not Kit), cast and actor, reset rungs `fixture < venue < game`, suite, batch. | `docs/writing-tests.md` |
| One word for a game copy in prose: `copy`; the option is `--instance`. | `docs/concepts.md` |

## One address for each kind of thing (`AGENTS.md` as a rule; detail where each lives)

Everything that is pointed at has one written form, the same in every file of the repository -
a test, a finding, a study, a document, a comment, an entry's evidence, a tool's answer.

| Thing | Its address | Example |
|---|---|---|
| Code of the game | the registry id: scope by `::`, a member by a dot | `esv::RollSystem::RollDice`, `eoc::HealthComponent.Hp` |
| A test, and what lies inside it | its path under `tests/` without the extension, then the case, then the step - one hierarchy, one separator | `_selftest/guide/attack_by_die`, `_selftest/guide/attack_by_die/die 20`, `_selftest/guide/attack_by_die/die 20/attack the goblin` |
| A verb of the API | as it is called | `GK.Act.Attack` |
| A command, a tool | as it is typed | `gatekit decomp code`, `bg3_events` |
| A kind of event | its name in the registry of kinds | `game_hit` |
| A file or a document | a path relative to the repository's root, as a link where the format has links | `docs/api-rules.md` |

- **In running text an address carries the prefix of its kind,** so that every mention of a
  thing is found by one plain search and a renaming is a search and a replacement:
  `code:esv::RollSystem::RollDice`, `test:_selftest/guide/attack_by_die/die 20/attack the goblin`,
  `event:game_hit`. The kinds whose form already begins with its own mark need none: `GK.` for a
  verb, `gatekit ` for a command, `bg3_` for a tool, and a path for a file. In a field of data
  whose kind is fixed by the schema (the evidence of an entry, the header of a test) the bare
  address is written, since the field says what it is.
- The toolkit does the search itself: `gatekit refs <address>` lists every place that cites an
  address, and moving a test or renaming an entry (`gatekit test move`, `gatekit registry
  rename`) rewrites its citations in the same act. After it the old address exists nowhere:
  no alias is kept, the check finds no citation of it, and the history of a name is in the
  commits. What an entry does keep is the name other sources give the same thing - Script
  Extender's, the Toolkit's - as a cross-reference by which a reader coming from there finds
  it; that is a fact about the thing, not a record of our renamings.
- An address is never an address in memory: `binary+0xRVA` appears only inside the registry's
  tables of builds and in what an instrument prints.
- A folder and a test of the same name do not stand side by side, and a case is not named like a
  step of its test, so that a test's address reads one way only.
- The checks prove it: `registry check` that every id cited resolves; `test check` that every
  test, case and step cited resolves; a link checker for paths.

## Rules for the API (`docs/api-rules.md`)

1. Universal, never a special case; tried on two unlike cases. Universality is asked of the
   API, not of a test's subject.
2. Every piece of work reviews the API: improve, remove, make faster, cover; the API gets
   smaller as well as larger.
3. Events, not waits. What by the game's logic ends, ends with an event; not having found it is
   not its absence. The event is looked for with the instruments before a read on the tick is
   written; a read on the tick is the last resort, recorded as a gap. Nothing polls without a
   listener; all reads run from one place on the tick, counted and timed; no verb keeps a timer
   or a tick hook of its own. A limit is a guard against a hang, in game time, never the
   ordinary end of a step.
4. Fair, cheat, observation. The namespace decides the class: `GK.Act` and `GK.Gui` hold only
   what goes the player's path; `GK.Cheat` holds the scripted twin under the same name; a fair
   name that is not built answers that it is not, and names both routes; no `force` flag. The
   class comes first in the name so that it can be withheld from a fair client. The registry of
   classes is data, held to the code by a unit test.

## The method (`AGENTS.md` as a rule; detail in `docs/writing-tests.md`)

- The first step, said explicitly: bg3.wiki is the base and always read first, and the search
  goes wider as part of the same step - patch notes and developer posts, community write-ups,
  forum threads where a behaviour was measured or argued, modders' documentation, Script
  Extender's documentation. The wiki states a rule; a thread gives a claim to test. Every
  source read is named in the study, no text is copied, and two sources that disagree are a
  finding worth a test.
- The order: the wiki and the wider search -> the mods that touch the mechanic (those kept in
  the home scanned with `gatekit mod usage`, then mods made for it, downloaded with consent,
  unpacked, read, never run) -> Script Extender, upstream and the fork -> the instruments ->
  the venue, the sandbox preferred -> what is left behind.
- Reaching the code is done with instruments, not guesses: the tracer (which components), the
  memory instrument (which function wrote the field; what depends on a value held), then
  reading and naming. A claim about code that an instrument could have checked and did not is
  not accepted.
- What to watch and hold comes from the earlier steps, as hypotheses stated with their source
  (the wiki, the owner's experience of the game, a mod) and then checked.
- Asking is a step of the method, not a failure of it. When the study has been made and the
  work still stalls, an agent does not guess blind: it writes down questions for a person who
  knows the game from playing it, and for the coordinator. A question is concrete and carries
  its context - what was read, what was tried and seen, what is expected, and what the answer
  would decide - so that it can be answered in a line; several are sent together, through the
  coordinator, who answers what it can and passes the rest on. The agent goes on with what does
  not depend on the answer. An answer is a hypothesis with its source ("the owner's experience
  of the game") and is checked like any other; it is recorded in the study with what the check
  showed. Open questions live beside the tests as rows of their own kind (`question`), so that
  a person can read them all at once and none is lost when an agent's run ends. The first pass
  over the wiki and the web does not always give the context; someone with a thousand hours
  often gives the lead at once.
- A case may close without its code - the last resort, after every step, never instead of
  them: tests and study stand, the code part is recorded open with what was tried, nothing is
  named on a guess.
- The last step checks Script Extender itself for errors in what it already declares; an error
  is held to the bar of a confirmed bug; "none found" is written down.
- What a case leaves is graded honestly: "not yet, and why" is a result.

## Tests (`docs/writing-tests.md`)

- First rule: no test for the sake of a test. A test answers a question about the game, states
  its rule, could fail, teaches a reader. The work is measured by what is understood, never by
  the number of tests.
- A test is a worked example a stranger runs with one command on a clean game with the kit.
- A test accounts for its own time. Finishing a test includes reading the time of its steps,
  naming the longest waits, and for each saying whether the rule needs it. A wait the rule does
  not need is removed - a line of dialogue skipped as a player skips it, a scene staged closer to
  the moment under test, a row waited for instead of a state read until it changes, resources
  given back instead of a turn ended and the enemies' turns sat through - or it is recorded as a
  gap with the reason it cannot go. The runner names the slowest steps and the time spent
  waiting, so the longest wait is seen without reading the test.
- A claim about what blocks or lets through is made only with the geometry and the state it was
  seen in, as preconditions of the test: where the thing stood and how it was turned, open or
  closed. Without them a result is not a finding.
- A test is about one thing. Variations of that one thing belong together: shooting a ranged
  weapon may run over every kind of ranged weapon - bows, crossbows and the rest - as rows of
  one test, each row reported by name. A different thing is a different test even when it
  looks alike: a ranged spell is not a ranged weapon. The line is the rule being checked - the
  same rule with other values is a variation, another rule is another test.
- A test does what it does once. Its verdict is a rule, and a rule shown once is shown; a
  pinned roll removes the reason to try again. Running the same thing a second time needs a
  stated and strong reason, written in the test: the thing tested IS the repetition (a scene
  staged and cleaned again and again - and then a handful of times, five and not a hundred),
  or the rule is about what changes between two runs. Variations of one thing are not
  repetition: they are different cases. A long run is a measurement, kept apart, asked for,
  and named as one. A test that has to be repeated to pass is a defect, in the test or in the
  API, never a setting.
- Hard against flexible. Hard is what guards against a false verdict: the class of a step, a
  clean scene, a pinned roll, and for attack tests a target that is hostile and answers with no
  dialogue. Flexible is how a test describes its scene and its preconditions: `need` takes any
  condition, no list of preconditions is required, and a shared one is made only when the same
  condition has been written by hand in a third test.
- Surprises in the event log may fail a test without the test listing anything: a blocking
  state it neither expected nor answered, a participant that is not of its cast, a row the host
  could not place. Every other row needs no accounting. A surprise that belongs is allowed in
  one line of the header or of `_suite.json`. (A report first; whether it fails a test is
  decided after the first night.)
- Nothing it does not need, rather than short; a plain cast.
- `step(name, class, fn)`: a failure names its step; a fair step that calls a cheat fails.
  `need(condition, why)`: unmet is a third outcome, not a failure.
- A composite test names the simpler tests it stands on. Two ceilings: a simple test's limits,
  and a composite's own, declared with the reason.
- Speed: a finished test runs at the working speed (one named value) and gives the same
  verdict as at 1; failing only when fast is first a suspicion against our API; a ceiling is
  declared only with the finding that proves the game is the cause; `independent` and `fixed`
  exist for whole families; a test records the speeds it is proven at.
- Rolls are decided, not hoped for: the pin, through the ledger.
- A scene is cleaned and verified after each test; a boost added outside the ledger is named.
- Never create and delete a creature in one Lua chunk.
- Findings live beside the tests: `about` in `_suite.json`, typed rows (`finding`, `bug`,
  `todo`, `curiosity`), read with `gatekit findings`. A `bug` is `suspected` until `confirmed`:
  the expected behaviour with its source, a test that shows it twice, a copy verified clean and
  bare, whose bug it is.

## The toolkit (`docs/concepts.md`, `docs/getting-started.md`, `docs/offline-databases.md`)

- One home; `gatekit.json` found from any checkout; `doctor` says what is where.
- A copy starts bare; mods come only as a named set. No command writes into the installed game
  unless it is named explicitly.
- `gatekit game verify` holds an install to a source of invariants; no source, no verdict of
  clean; every database records the build it was made from.
- One event log, one stream for the caller, attention as its view; families are data; the host
  follows the registry the running game loaded and never drops a row.
- The memory instrument and `decomp at`; the tracer with its frame and flush stamps.
- The deck holds several copies in one window.

## The README

The banner first. One sentence of what it is. Requirements. A safe quick start of five lines,
with `--instance`. Three ways in: drive the game, write a test, read the code. No dated counts,
nothing that does not exist.

## The proof of concept (its briefs and the coordinator's first message)

- A new, clean coordinating session; three executors with a copy and files of their own:
  rolls; attacks and then fair movement and the opportunity attack; aiming and what blocks it
  (and the fair `AimBest`).
- Executors write to the coordinator when it matters - blocked, a finding that changes
  another's work, a question worth an hour - and gather the rest for a natural pause. No
  progress reports for their own sake.
- The coordinator may change the design documents and the shared API over the executors when a
  wish holds across the game, and records each decision with its reason.
- When an executor's queue is done it reports and proposes where it would dig next, as
  particular as it likes; the coordinator lets it go on when it is likely to succeed, stays in
  its own files and copy, and has a "done" - and never lets through a test for the sake of a
  test.
- In the morning one summary, and nothing lost: every finding, bug, wish and to-do of the night
  is in the findings or the open items, checked against the reports line by line. Work is
  committed as it goes. Nothing is pushed.
- What is already proven is said, so that it is not ordered again.
- The descent into the code is not the last rung of a queue. "Done" for a rung asks for an
  instrument used and a name given, or the stated reason why not: in the first night no executor
  reached the code until asked, because the briefs put it last and required nothing of it.
- A fair `GK.Gui.AimBest` is blind exactly as a player is: it leaves the creatures the party
  does not see out of the area it weighs and out of the line of the aim alike, so an aim it
  chose may be stopped on the way by one it could not count - and that is the right outcome.

## Not in the documents: the open items

Kept in one ledger (`research/open-items.md` after the rewrite), not as prose: the high-priority
defect of a copy's build against the default; surviving a crash (watchdog, dump, warm standby
for dangerous research); the bridge's own timers; the study of simple actions that take time
(sheathing; interrupting it); the sandbox that looks like a test level; the default cast (women,
dressed, plain); short films of telling tests in a repository of their own; the settings windows
as ground for the memory instrument; how a modder finds the tests; fair forms of the scripted
actions; cases inside a test, independent and reported by name (high priority, the next thing
built); tests in parallel by several scenes in one copy; the study of more than one player in
one game (a second local player, a second copy over the network, a network player faked inside
one client); what a neutral creature does when attacked; the wait that settles the weapon
after a fight, to be replaced by the game's own event.

## How strict the toolkit is (`AGENTS.md`)

- Strict where it protects the two key products: the tests and the registry of the machine code.
  Everywhere else the lighter way is taken; no rule, check, schema or refusal is added without
  saying what it protects and what it costs each time something changes, and one that costs more
  than it saves is removed.

## How this file is kept

Every decision of the owner's about how tests are written, how the API is shaped or how the
work is carried out is written here when it is made, in the section of the document that will
own it - not left in a plan, a report or a conversation. The plans say what is done and in
what order; this file says what the documents must end up saying.
