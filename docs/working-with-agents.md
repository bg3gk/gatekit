# Working with agents

How a long run of work on GateKit is organised when AI agents do it with no person watching: one
**coordinator** and a few **executors**. The rules of the repository still hold for every one of
them ([AGENTS.md](../AGENTS.md)); this page is how the work is divided, handed out, checked and
kept. It is for an agent that will coordinate, and for a person who starts one.

## Roles

- **The coordinator** holds the run. It builds only what the others depend on - a verb of the
  toolkit, a command, a change of the Script Extender fork, a merge - accepts the executors'
  reports, writes everything down and decides the next package. It steps into a package only
  when it is hard. It is the only one that commits, and the only one that asks the owner.
- **Executors**, at most as many as the machine and the usage limits allow (two on an ordinary
  workstation), each running one package at a time. An idle slot is filled as soon as a package
  worth doing exists; more than the limit is never started.

The kinds of package that work:

| Kind | Does | May change |
|---|---|---|
| recon | reads the decompiler and probes the running game; names functions in the registry's live layer; reports | nothing in the repository |
| tests | writes the tests of one question | one folder under `tests/` |
| fenced code | builds a named piece | a named new file and named places, nothing else |
| sweep | holds the game's numbers against the wiki or the data | nothing; a report and its tables |
| mods | reads how other mods solve a thing | nothing |
| documents | brings documents up to date | the documents named |

Recon and building are separate packages: first how the game does a thing, with names; then the
verb built on it. The first way tried is always the game's own function; a workaround comes only
after that is shown impossible.

## A package

A package is a narrow question with the proof asked for: not "look at rolls" but "which function
decides that a roll is needed, shown by a probe on two unlike rolls". A report's "not done, and
the exact stopper" is the next package's question, so packages chain on findings.

The prompt of an executor, in this order:

1. Who it is, and the common instructions to read first: the tools, the rules, the order of help,
   the rule on blocking windows (below). One file in the notes folder, kept current through the run.
2. The blocking windows known so far, each with the call that closes it, and the traps found so far.
3. Its own game copy (`--instance <name>`), its name for the live layer (`GATEKIT_WHO`), and the
   exact start line with the Script Extender build folder.
4. Its fence: what it may edit (nothing, one tree, a named file), "no commit", the commands it must
   not run (`bridge-reload`, `decomp refresh`, `registry commit`), "Edit and Write only, no
   scripts" ([AGENTS.md](../AGENTS.md), rule 10), "no waits: react to the game's events"
   ([api-rules.md](api-rules.md#events-not-waits), "a wait is a smell"), and "close the game at the end".
5. The question in one paragraph, with everything already known: ids, addresses, the files and
   reports of earlier packages to read first, so that nothing is found twice; and the instruments
   that answer it - watches, probes up and down, coverage and its comparison of an action with an
   idle scene, the start's timeline ([writing-tests.md](writing-tests.md#the-instruments-at-hand)) -
   named, since a package not told of them reaches for none.
6. Sub-questions in order of worth, each with how to prove it (a probe, a pinned die, both speeds)
   and a time box.
7. The report's shape and length: the answer first, then the evidence and the commands that
   reproduce it, what stayed unknown, the ids noted, the windows met, which steps of the order of
   help were used, the gaps of the toolkit.

A wish of the owner that arrives during the run becomes a package, or an amendment sent to the
executor already in that area, with the order of worth restated so that it does not eat the package.

## The order of help

On a problem about the game - a mechanic not understood, a scene that will not stage, a number
that does not add up - an executor goes through these in order before it asks:

1. the wiki (`gatekit wiki`);
2. the wider internet: forums, Script Extender's documentation and source, bug reports;
3. the mods on disk (read, never run);
4. the coordinator, with one short question and what 1-3 gave.

The owner is asked last, by the coordinator only, when all of the above failed. Questions for the
owner are kept as numbered yes-or-no items, so that one line answers them, and the work goes on past
them.

## Blocking windows

An action in the game can call up a hint, a tutorial or another window that blocks the game until
it is closed. It is never swallowed or worked around: the executor finds it (`status`, `shot`,
`GK.Gui.Stuck`, attention), writes down which action called it, what the window is as the game
names it, whether it blocks and the call that closed it, closes it, and lists it in its report.
Each such window is a candidate for the attention system
([contracts/bridge-protocol.md](contracts/bridge-protocol.md#attention)), so that tests and tools
later handle it by themselves.

## Game copies and windows

- One copy per executor, named in its prompt; never the coordinator's, never the installed game.
- Every copy's window takes a free cell of a grid on the screen, never on top of another's, so that
  a person watching sees each copy and the real pointer rests outside every frame (the pointer
  drags the game's aiming and hover). No more copies are started than the grid has cells.
- A copy is started once for a package and kept: another level or save is loaded in it
  (`reset --save`, `test` between groups), and it is started again only for a new Script Extender
  build, a test of the start itself (`tests/startup`), another mod list, or a game that died.
- A copy is closed at the end of its package (`gatekit --instance <name> down`).

## The accept loop

A report is a claim. Each one goes through the same loop before the next package:

1. **Verify** in the coordinator's own copy: run the tests the report names, at both speeds where
   speed matters; make one direct call of a new verb. What could not be verified is said, and not
   merged.
2. **The registry**: `gatekit registry clashes`, `registry commit`, `registry reanchor --target
   bg3.exe`, `registry places <file>` for a data file whose places moved, `registry check` with no
   refusal, `registry digest` to read what the run wrote. A pattern a package added to the Script
   Extender fork's `BinaryMappings.xml` is the registry's (`registry mappings emit`, a global with
   `--via`), never written by hand: after the reanchor, `registry mappings sync <BinaryMappings.xml>`
   writes it again checked in every executable, and `sync --check` is clean before a build.
3. **The study**: the finding written into its study under `research/studies/`, and what is still
   open into [research/open-items.md](../research/open-items.md).
4. **Commit**, by path, with the trailer of [.github/thrall.md](../.github/thrall.md).
5. **The next package.**

A finding that contradicts an earlier one is taken back in writing in the study, never overwritten
in silence.

## Builds

- A new build of the Script Extender fork goes into a new folder of the home's `se-builds`
  (`v34-<name>`); the executors are told the folder; the old ones stay.
- A build is tried before it is handed out.
- A worktree an executor is running from is not rebuilt under it. With two lines of work at once,
  each has its own worktree and its own build of the toolkit; changes are ported between them by
  hand and verified.
- The mod's Lua is never left broken while an executor may start a game: edit, then test at once;
  never edit a file a fenced executor works in.

## What is written where

| What | Where |
|---|---|
| a name of the game's code, with its evidence | the registry (the live layer during the run, committed by the coordinator) |
| how a mechanic works, what was tried | its study, `research/studies/` |
| what is still to do, and why not yet | [research/open-items.md](../research/open-items.md) |
| a rule of a test, its scene | the test's leading comment |
| the run's chronicle: times, packages, reports, the owner's messages, the morning report, the prompts | the notes folder, outside the repository (`<notes folder>`) |

The chronicle is written at once - facts, addresses, what is next - with times from the clock. It
is what the coordinator reads first after a break or a loss of context. Nothing of it enters the
repository's documents ([AGENTS.md](../AGENTS.md), rule 11).

## Timers and lost copies

Every half hour the coordinator checks who works, which game processes have no package (a lost
copy is closed), and the free memory. A package past its time box is asked for what it has.

## Traps

- Executors forget the rule of no scripts unless the fence says "Edit and Write only".
- A commit right after an unfinished `git cherry-pick -n`, or `git add -A`, takes an executor's
  unfinished files: unstage, then add by path.
- A unit run with `--no-build` tests the old build's embedded data: it proves nothing after a JSON
  change.
- A schema keyword the toolkit does not implement breaks other commands for everyone: check a new
  schema at once.
- An executor's unfinished tests cite ids it has not noted yet, so `registry check` refuses them:
  commit the registry alone and leave the tests to their package.
- `Ext.Events.<name> ~= nil` is always true: ask whether an event exists by `GK.Verbs.Has`.
- A wrapper of every verb must not call a wrapped verb.
- A tool that is not aware of the screen's scaling reads the wrong size for a window.
