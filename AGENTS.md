# Working in this repository

What an AI should know before changing GateKit: where each thing lives and the rules the project
holds itself to. The nearest `AGENTS.md` to the file you are editing wins; `CLAUDE.md` points
here. How the pieces fit together is [docs/concepts.md](docs/concepts.md); what the work is aimed
at is [docs/design/what-this-is-for.md](docs/design/what-this-is-for.md).

"Agent" means three things around GateKit, each with its own page:

- **an agent working on GateKit** - you, if you are reading this to change it;
- **an agent using GateKit** to drive the game from outside → [docs/driving-the-game.md](docs/driving-the-game.md);
- **an agent playing inside the game**, a character or a side in combat →
  [docs/design/ai-controllers.md](docs/design/ai-controllers.md).

## Where things live

| If you are changing… | Look in | Its contract |
|---|---|---|
| how the host talks to the game | `src/GateKit.Bridge`, `mods/GateKit/.../Shared/Bridge.lua` | `docs/contracts/bridge-protocol.md` + `schemas/bridge/` |
| starting the game, copies, backups | `src/GateKit.Copies`, `src/GateKit.Platform` | `docs/contracts/cli.md` |
| where GateKit keeps things | `src/GateKit.Home` | `docs/design/home.md` |
| commands of `gatekit` | `src/GateKit.Cli` and each project's `CreateCommand()` | `docs/contracts/cli.md` |
| our reading of the game's code: functions, types, globals, where each is in every build | `registry/`, `src/GateKit.Symbols/Registry` | `registry/README.md` + `schemas/registry/` |
| publishing or fetching a built database | `src/GateKit.Release`, `index-release.json`, `publish/map/` | `docs/design/what-we-publish.md` |
| tools an agent sees | `src/GateKit.Mcp` | `docs/contracts/mcp.md` |
| the `GK.*` API inside the game | `mods/GateKit/.../Lua/` | `docs/contracts/lua-api.md`, `lua-types/GateKit.lua` |
| in-game windows | `mods/Weave` | `lua-types/Weave.lua`, `mods/Weave/README.md` |
| reading or writing any JSON file or tool answer of ours | `src/GateKit.Json` (`JsonText`, `SchemaSet`) | `docs/contracts/data-format.md` + `schemas/` |
| tests (they run in the game) | `tests/` (a tree by the game's notions), the runner in `src/GateKit.Testing` | `docs/writing-tests.md`, `docs/contracts/cli.md` (`test`) |
| unit tests: checks of the toolkit itself, with no game | `unit/`, projects named `*.UnitTests` | `dotnet test` (Checking your work, below) |

**The contracts in `docs/contracts/` are the source of truth.** Change the contract in the same
commit as the code, or do not change the behaviour.

## Rules

1. **English only** — code, comments, documentation, commit messages. Localized text comes from
   the game's own data, never a hardcoded table. The exceptions are in `CONTRIBUTING.md`.
2. **Name things as the game does.** Where the game has its own name for a field or a function,
   that name wins; the names other sources give the same thing (Script Extender's, the
   Toolkit's) stay as cross-references (`known_as`), never as aliases (rule 15).
3. **Nothing of Larian's is committed**: no game data, binaries, decompiled code, fonts or
   textures. Anything derived from the game is generated on the user's own machine.
4. **Cross-platform.** Operating-system specifics go behind `IGamePlatform` in
   `src/GateKit.Platform`; nothing else may use Windows-only APIs. No PowerShell, no `.cmd`.
5. **No machine-specific paths.** Settings come from environment variables, `gatekit.json` or
   discovery; `gatekit doctor` explains what is missing.
6. **Prefer a programmatic bridge to a screenshot.** Anything that can be read or pressed
   through the game's own code should be, not through the window.
7. Warnings are errors (`Directory.Build.props`). Keep it that way.
8. **Never sign as yourself.** Whichever assistant you are, you are not a commit's co-author by
   name: the trailer is the bot's, as [.github/thrall.md](.github/thrall.md) gives it. The hook
   rewrites it and CI refuses it otherwise.
9. **Sign what goes out under the bot's name.** Drafts you write for a pull request, commit,
   issue or discussion end with the line in [.github/thrall.md](.github/thrall.md) — last, after
   any trailers. A person reads every draft before it is posted, and text written by a person as
   themselves does not carry the line.
10. **One language, and the toolkit first.** The project is C#, with Lua inside the game. Do the
    work through `gatekit` and its MCP tools wherever they can do it. Where they cannot, that is
    a gap to close in the toolkit, in C# — not a reason to script around it. Write no scripts in
    another language (Python, JavaScript, shell beyond a single command), not even throwaway ones
    in a temp folder: nobody can run them again, and a number in a report has to come from a
    command or a test that is in the repository. Change files with your own editing tools,
    never with a program that rewrites them.
11. **Write what is, and nothing else.** Documents, comments, help texts and the descriptive
    fields of data are read rarely; every sentence must earn its place. State how the thing works
    now. Leave out:
    - **time and history** — dates, "today", "was", "used to", "now moved", how a thing came to
      be, which run, session, agent or branch found it. History lives in commit messages;
    - **the story of the work** — what was tried, what failed first, who asked, what was decided
      in which conversation. Keep a rejected alternative only when its reason stops the mistake
      being made again, and then in one sentence;
    - **numbers that go stale** — counts, sizes, timings, percentages, "N of M so far". A number
      stays only when it is the point, and then with the command that reproduces it;
    - **repetition** — one place per fact; elsewhere, a link. Do not restate a rule, a path or a
      definition that another document owns;
    - **the obvious and the hedge** — a comment that repeats the code, "note that", "it is
      important", apologies, promises of future work without an owner, praise of the result;
    - **machine and people specifics** — this machine's paths, a person's name, a model's name;
    - **the words the project does not use** — listed where each word is owned:
      [docs/writing-tests.md](docs/writing-tests.md), "Words";
      [docs/api-rules.md](docs/api-rules.md), rule 4; [docs/concepts.md](docs/concepts.md), "Copies".
    Plain professional prose, short, in the present tense. A work record that is a chronicle by
    nature (a run's log, a wish with its date) is data under `research/` - what is still to do is
    the open items, `research/open-items.md` - not prose in a document. If removing a sentence loses nothing a reader needs to use or change the thing,
    remove it.
12. **Hold the API to its four rules**: universal, never a special case; every piece of work
    reviews the API; events, not waits; every verb fair, cheat or observation, the namespace
    deciding which. → [docs/api-rules.md](docs/api-rules.md)
13. **Study a mechanic in order**: the wiki and the wider search, then the mods that touch it,
    then Script Extender, then the instruments (the tracer, the memory instrument), then the
    venue, the sandbox preferred, and last what is left behind. A claim about code that an
    instrument could have checked and did not is not accepted. → [docs/writing-tests.md](docs/writing-tests.md)
14. **No test for the sake of a test.** A test answers a question about the game, states its
    rule and could fail; the work is measured by what is understood, never by the number of
    tests. → [docs/writing-tests.md](docs/writing-tests.md)
15. **One address for each kind of thing.** A test with its cases and steps, the game's code, a
    kind of event and a file each have one written form, the same in every file; in running text
    an address carries the prefix of its kind (`test:`, `code:`, `event:`), so that a renaming is
    one search and one replacement, and an old name is kept nowhere as an alias.
    → [docs/writing-tests.md](docs/writing-tests.md), "Addresses"
16. **The API and the tests change in `main` by the owners' word.** A change of the API inside
    the game - a verb added, removed or given another meaning - and a test added to the tree may
    be proposed complete and ready: written, checked, with its documents, on a branch of its own
    or as a pull request. It enters `main` only after the explicit consent of the repository's
    owners. Until then it is a proposal, and nothing else is built on it as if it were decided.
    A proposal serves one of the project's three aims and says which: mapping the game, making
    it easier for modders to extend, or automating it. It repeats nothing the toolkit already
    has: before a verb, a command or a test is proposed, what exists is searched, and what is
    found is used or extended in place.

## Checking your work

```bash
dotnet build                               # the whole solution
dotnet test                                # the unit tests (unit/), no game needed
./gatekit --instance <yours> test tests    # the tests (tests/), in a game copy
```

**Never touch the installed game or a copy that is not yours.** Always pass `--instance <name>`:
what a copy is and why it is safe is in [docs/concepts.md](docs/concepts.md). Close every game you
start (`gatekit --instance <name> down`).

Tests need GateKit's own Script Extender build (`gatekit se install`, or `GATEKIT_SE` pointing at
a build folder); on the stock extender the bridge stays silent.
