# Documentation

| | |
|---|---|
| [getting-started.md](getting-started.md) | from nothing to your own mod reloading live in the game |
| [concepts.md](concepts.md) | how the host, the channel, the in-game mod and the extender fit together |
| [driving-the-game.md](driving-the-game.md) | connecting an AI agent and driving the game from outside |
| [contracts/](contracts/) | the exact reference: the bridge protocol, the CLI, the MCP tools, the Lua API |
| [design/](design/) | what is planned and why, written before it is built: [AI controllers](design/ai-controllers.md), [coverage runs](design/coverage-runs.md), [the deck](design/deck.md), [the search site](design/map-site.md), [what we publish](design/what-we-publish.md) |

## What we worked out about the game

Not about GateKit — about Baldur's Gate 3 itself, measured or found while building these tools.

| | |
|---|---|
| [facts.md](facts.md) | what was measured about the game, the Toolkit and Script Extender |
| [levels.md](levels.md) | the levels a test game can start in |
| [agent-rules.md](agent-rules.md) | where the game's rules differ from D&D 5e |
| [curiosities.md](curiosities.md) | findings a player might enjoy, including content that was cut |

## A note on the word "agent"

It means three things here, and they have three homes:

- an agent **changing GateKit** → [../AGENTS.md](../AGENTS.md);
- an agent **using GateKit** to drive the game → [driving-the-game.md](driving-the-game.md);
- an agent **playing inside the game**, a character or a side in combat →
  [design/ai-controllers.md](design/ai-controllers.md).

Open work is listed in [../research/TODO.md](../research/TODO.md). Working material that is not
documentation — journals, half-finished tools, measurements still
being argued about — lives in [../research/](../research/).
