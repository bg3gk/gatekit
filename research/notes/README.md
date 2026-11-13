# notes — directions that were closed, and what was left in them

On 2026-11-12 every direction but two was closed, and each session wrote down what it had not
finished before it stopped. These are those lists, kept verbatim. They are stale by design: they
describe the old repository's layout and the old Python tools, and some of what they ask for has
since been built. Read them as a record of intent, not as a plan.

| | |
|---|---|
| [`stand-todo.md`](stand-todo.md) | the test stand, the MCP server and the official Toolkit bridge: what was left undone |
| [`console-todo.md`](console-todo.md) | the in-game console (F11) |
| [`weave-todo.md`](weave-todo.md) | [Weave](../../mods/Weave), the language for windows inside the game |
| [`console-research.md`](console-research.md) | a survey of what comparable in-game consoles and overlays do well, and what of it the GateKit console took. Not a TODO — the reasoning behind the console's design, and the only one of these worth reading on its own |

Two notes from the old `docs/` were deliberately **not** brought:

- `rename-gatekit.md` — the table that renamed DevKit to GateKit everywhere. The rename is done
  and this repository carries none of the old names, so the table has nothing left to say.
- `ui-catalog.md` and the catalogue behind it — generated from the game's own interface files.
  Nothing derived from Larian's data is committed; `gatekit` builds it on your own machine.
