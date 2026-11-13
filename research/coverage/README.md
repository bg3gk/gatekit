# coverage — mapping the engine's components from a running game

A paused session's working tools. It drives Baldur's Gate 3 into one situation after another and
watches which of the engine's ECS component types come to life, what their memory holds, and how
each field is laid out — so that GateKit's Script Extender build can declare them and Lua can
read them. The method and its rules are in [design.md](design.md) (Russian), what the session
learned about working that way is in [journal.md](journal.md) (Russian), what it suspects but has
not confirmed is in [leads.md](leads.md), and how a batch is written up is in
[report-notes.md](report-notes.md).

## It still needs the old Python stand

**These tools were not ported to C#, and porting them is open work.** They import the old
repository's Python stand (`stand/gatekit.py`, `stand/instances.py`), its offline query tool
(`kit/gatekit.py`) and its symbol database. The stand itself has been rewritten as
`src/GateKit.Stand` and the offline tools as `src/GateKit.GameData`, but the call shapes differ,
so nothing here runs against the C# host yet.

To resume this work you need a checkout of GateKit's first repository (the private archive) and:

```
set GATEKIT_OLD_REPO=<path of the old repository>
set BG3_SYMBOLS_DB=<path of symbols.sqlite>     # optional; default <old repo>\symbols\symbols.sqlite
python research/coverage/clean_stand.py up --level GustavX:TUT_Avernus_C
python research/coverage/clean_stand.py run research/coverage/collect.py
python research/coverage/clean_stand.py down
```

[`../oldrepo.py`](../oldrepo.py) is the one place that resolves those paths; every tool that needed a
path outside its own folder now goes through it. Porting them means replacing the
`import gatekit` / `import instances` calls with the C# host's command line or MCP tools — the
rest of each tool is plain data work and would survive unchanged.

Two more external dependencies the old repository held and this one does not: `se-fork/reference/`
(other projects' sources, read-only, never committed anywhere) used by `compare_macos.py`,
`fieldmeta_check.py` and the `macos_*.py` tools, and the fork's own source tree, which
`apply_game_names.py` edits in place and which now lives in
[bg3gk/bg3se](https://github.com/bg3gk/bg3se).

## What is here

| | |
|---|---|
| `*.py` | the tools: `clean_stand.py` (an instance with only the bridge), `collect.py` (what the extender covers), `probe.py` (raw bytes of a component), `capture.py` / `seen.py` / `tour.py` (standing capture while the game is driven), `check.py` (layouts against the running game), `generate.py` (C++ declarations), `tags.py`, `promote.py`, `quarantine.py`, `todo.py`, `wiki.py` (bg3.wiki as context), and the `macos_*` tools that compare against the macOS build's symbols |
| `lua/` | the Lua the tools inject: `probe.lua` (the Probe library), `census.lua`, `onebyte.lua`, `se_suspects.lua` |
| `layouts/` | the confirmed field layouts, one JSON per component type, in `alive/` and the batches around it — the session's main product |
| `coverage.json` | the full index of what Script Extender covers, rewritten by `collect.py` |
| `se-coverage.md` | the human summary of `coverage.json`, rewritten by `collect.py` |
| `systems/survey.csv` | what covering each of the game's 934 ECS systems would take |
| `levels.json` | which levels can be started cold, measured by `levels_survey.py` |
| `tours/*.json` | the tours: a list of situations to play, as data |
| `plan.md`, `systems-plan.md` | the plans these numbers feed |

## What was left behind on purpose

`seen/` — about six megabytes of component samples captured from a running game. It is derived
from Larian's data and is not committed anywhere; `seen.py start` builds it again from your own
copy of the game. `work/` was scratch and is gone too.

## seen/

`seen/index.json` — for each of the 2,066 engine component types we have mapped, where it was
actually observed: the kinds of entity, the levels, the largest count seen on one entity. It is
how a layout claim is backed by a sighting, and how coverage knows what is still unseen.

It is published deliberately: it is our measurement, and the game's own identifiers appear in it
only because a sighting cannot be described without naming what was sighted.
`docs/design/what-we-publish.md` explains where the line is. The per-side raw captures
(`seen/client`, `seen/server`, about 22 MB) are not here: they are the working output of
`seen.py` and are rebuilt by running it.
