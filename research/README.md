# research — working material, not documentation

This folder holds what the people and agents building GateKit work *with*: half-finished tools,
measurements, journals, plans and notes from sessions that are paused or closed. It is kept in
the open because throwing it away would mean doing the work twice, not because it is finished.

**It is not documentation and not product.** Documentation is [`docs/`](../docs); the exact
reference for every interface is `docs/contracts/`. Nothing here is supported, nothing here is
promised to keep working, and a file here may contradict the documentation — when it does, the
documentation wins.

Two rules of the repository are relaxed inside this folder, and only inside it:

- **Language.** A few notes are still in Russian. Each one starts with an English line saying
  what it is and that it is untranslated. Everything new is written in English.
- **Python.** Most of the host has been rewritten in C#; some research tools are still Python
  that runs against the *old* repository's stand. Where that is so, the folder's own README says
  it.

The rule that is **not** relaxed: nothing of Larian's is committed here either — no game data,
binaries, decompiled code, fonts or textures. See [CONTRIBUTING.md](../CONTRIBUTING.md). A tool
that needs such a file generates it on your own machine from your own copy of the game.

| Folder | What is in it |
|---|---|
| [`coverage/`](coverage) | the paused session that drives the game to map the engine's ECS components: about 30 Python tools, the component layouts it confirmed, its journal and its leads. Still needs the old Python stand — see its README |
| [`se-fork/`](se-fork) | the state of the Script Extender fork work: what was added to the extender, what is left, and the build scripts. The fork itself lives in its own repository, [bg3gk/bg3se](https://github.com/bg3gk/bg3se) |
| [`perf/`](perf) | profilers for start-up and frame time, the `fastexists` C++ prototype, and where the game's start-up time was measured to go |
| [`symbols/`](symbols) | reverse-engineering notes: what the Toolkit's export tables give, what the official Toolkit's managed code looks like, and the Ghidra pseudo-code tooling |
| [`offline/`](offline) | parts of the old offline tools that the C# ports do not cover: the static-site export, the query guide written for language models, mod studies, and a Rust `.lsf` reader |
| [`agent-play/`](agent-play) | an agent playing the prologue without screenshots: journals of the runs, the wishes they produced, and the briefs the agents were given |
| [`probes/`](probes) | one-off Lua probes written to answer a single question inside the game |
| [`notes/`](notes) | what is left undone in directions that were closed, and a survey of other projects' in-game consoles |

## Where this came from

All of it was moved out of GateKit's first repository when GateKit became this monorepo. Paths
that pointed at one machine were replaced with environment variables or with `<a placeholder>`,
and links were repointed where the file they named came along; nothing else was rewritten, so
the notes still read as they were written, dates and all.

A note that still mentions `docs/api.md`, `docs/situation.md`, `docs/stage.md`, `docs/windows.md`
or `docs/toolkit.md` means the old repository's description of the Lua API, the honest-play
commands, staging, the game's windows and the official Toolkit. All five were superseded by
[`docs/contracts/`](../docs/contracts) and did not come; read the contract instead.
