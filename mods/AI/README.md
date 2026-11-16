# GateKit AI

A bridge for an AI agent that plays Baldur's Gate 3: four tools, each answer carrying what
changed in the game since the agent's previous call, so that the agent acts and never has to
ask the same thing twice. It is the tool side only. The model, its application, voice and the
conversation with the owner are the application's business; nothing here names or needs a
particular vendor.

**Who installs it:** whoever lets an agent drive a GateKit game copy. It is a mod that depends
on GateKit (`meta.lsx` lists the GateKit bridge under `Dependencies`) and is built from GateKit's
verbs only. Folder `Gwynerva_GateKitAI`, UUID `a1c0de57-6b1d-4a17-9e2b-0a1b7e57a11e`, mod table
`GateKitAI`. `gatekit` installs every mod under `mods/` into a copy, this one after GateKit;
a copy started from a bridge source without it works as before.

## Layout

| Path | What it is |
|---|---|
| `tools/ai.json` | the four tools, defined once: name, description, plain JSON Schema, and the one Lua call each makes. Every front is served from this file |
| `brief.md` | how to use the tools: a plain text for the model, as a system prompt or as the first message |
| `examples/persona.md` | an example of the owner's own prompt (who the character is, what is wanted) |
| `Mods/Gwynerva_GateKitAI/ScriptExtender/Lua/Server/` | `Bridge.lua` (the table `AI`, the rows, the channel to the client), `Mode.lua` (the mode and the session), `Say.lua` (the line over a head, the accent, what a target names), `Plan.lua` (the route plan, the notes), `Now.lua` (the `now` block), `Step.lua` (the four tools) |
| `.../Lua/Client/Bridge.lua` | what only the client knows (open windows, dialogue answers) told to the server; the player's own clicks carried out in the UI's context |
| `.../Lua/Shared/` | `Scenes.lua` (scene states read generically), `EventKinds.json` (the mod's rows), `VerbClasses.json` (each verb's class) |
| `.../Lua/Plans/` | route plans by name |

`AI` is a global of the mod's own Lua environment, as `Weave` is of Weave's; from GateKit's
host and from other mods it is `Mods.GateKitAI.AI`. Its rows are a family of its own, `ai`,
added to GateKit's event log at start with `GK.EventLog.Register`. Annotations:
`lua-types/GateKitAI.lua`. Tests: `tests/ai/`.

## The four tools

| Tool | Does |
|---|---|
| `ai_session { mode, prompt, prompt_file, plan, step }` | begins a session and answers the owner's prompt and the whole picture; with no argument reads the session |
| `ai_step { do, target, spell, n, who, say, think, note }` | one act, the wait until the game has settled, what changed |
| `ai_look { part, detail }` | the whole picture or one part, when the agent is lost |
| `ai_scene { name, call }` | the owner's director commands: a named call of a staged scene |

Every answer is one line of JSON with the keys `ok`, `mode`, `did`, `why_not`, `hint`, `now`,
always all of them and in that order (`prompt` before `now` where a tool gives it). A refusal is
`ok = false` with nothing done, `why_not` saying why and `hint` giving the corrected call.

### `now`: only what changed

The bridge keeps a cursor: what the agent was last told. A key of `now` is present only when
its part changed, so a call after which nothing happened answers `"now":{}`.

| Key | Holds | Told again when |
|---|---|---|
| `where` | the level, the nearest named thing, the controlled character and its position | it moved two metres or changed level |
| `fight` | `off`, or the round, whose turn is open (`(mine)`: a party member's), the story's countdowns | any of it changed |
| `can` | for each enemy the party perceives, the best actions of the member whose turn is open, with the game's own chance | a party member's turn opens |
| `party` | each member: hit points, what is in hand, conditions | any of it changed |
| `waiting` | what the game waits for from the player - a dialogue with its numbered answers, a roll window, a tutorial, loot, a prompt - each line ending with the step that answers it; `[]` when it is gone | it changed |
| `near` | creatures and usable things the party perceives, each with a handle (`#` and six characters of its uuid), how it stands to the party, distance, bearing | `+` new, `~` changed |
| `heard` | lines spoken | since the cursor |
| `events` | hits, deaths, journal news, one line each | since the cursor |
| `rolls` | open mode: every roll thrown | since the cursor |
| `scenes` | open mode: the state of each table under `GKScene` | it changed |
| `route` | open mode: the plan's next step | it changed |
| `notes` | the agent's own notes written within fifteen metres | on coming near |
| `more` | how many entries of a list were left out | - |

**Size.** A block has a hard cap of characters as JSON text, `AI.Detail.brief.chars` (1200),
and lists of at most `AI.Detail.brief.list` (6) entries. A block over the cap is cut in a fixed
order: rolls first, then what is around, heard and happened, each with its count in `more`;
`waiting`, `can` and `party` - what the agent acts on - are cut last. `ai_look` with
`detail = "full"` uses `AI.Detail.full` (6000 characters, lists of 30). Both tables are data.

### Settled

A step answers when the act has ended and then either something waits for the player's answer,
or for `AI.Quiet` (8) server ticks in a row no line is being spoken, no film plays, the game is
not loading, in a fight a party member's turn is open, and the controlled character does not
move. So a walk answers when the character stands, a talk when the dialogue waits, an ended turn
when the enemies have taken theirs. The wait is counted in the server's ticks, never in seconds;
its limit is `AI.Cap` (1200 ticks), past which the answer says what still goes on as a line of
`waiting` ending in `do=wait`.

### The acts

| `do` | Way | Through |
|---|---|---|
| `answer`, `continue`, `roll`, `take`, `close` | fair | `GK.Gui.Resolve` |
| `cast`, `attack` | fair | `GK.Act.Cast`, `GK.Act.Attack` on the client: the hotbar, the aim, the click |
| `end`, `select`, `wait` | fair | `GK.Act.EndTurn`, `GK.Act.Select` |
| `go`, `use`, `talk`, `loot`, `jump` | ordered | `GK.Cheat.Move`, `Use`, `Talk`, `Loot`, `Jump`: GateKit's scripted order. The character walks, reaches and uses as the game makes it, but the player's own click for these is not built in GateKit; when it is, these acts move to it |
| `cheat_cast`, `cheat_teleport` | cheat | `GK.Cheat.Cast` (a spell the hotbar does not hold), `GK.Teleport` |
| `plan` | - | the act the loaded plan names for its step (open mode) |

Each step's row says which way it went (`way`). `AI.Acts` is data: an act is a row of it.
`say` shows a line over the character's head with a glow on the target (`AI.Say`); `think` goes
to the row only; `note` keeps a note with the place.

## Two modes

The mode is set by `ai_session` and named in every answer (`mode`). It is decided in one file,
`Server/Mode.lua`, which every part of the bridge asks; GateKit already classes what it reads
the same way, by a reader's scope (`seen`, `known`, `all`) and a row's `knows`.

| | `blind` | `open` |
|---|---|---|
| creatures and things in `near` | what the party perceives now | the same; a look also lists what is within the radius and not perceived, marked `unseen` |
| a target of an act | a party member, what is perceived now, what was seen earlier on the level; anything else is refused: "not knowable to a player: ..." | anything on the level |
| rows of the log in `events` | those a player could know (`knows = player`) | all |
| dice | only the game's own roll window, in `waiting` | every roll thrown, in `rolls` |
| `scenes`, `route`, `do = plan`, a plan | not told, refused | told |
| `ai_scene` | a call the owner names is run; nothing of the scene is read back | lists, reads and runs |
| the agent's notes, the owner's prompt, lines heard, the party, `can` | told | told |

Blind mode is what a player at the screen has: the hit chance of `can` is the tooltip's, an
enemy's hit points are its health bar's. What it does not make blind: the level's internal name
in `where`, and positions as coordinates.

## The owner's prompt

`ai_session` takes the owner's own text for the model - the character's personality, what is to
be done, how to answer in dialogues, lore - as `prompt` (text) or `prompt_file` (a path). The
bridge reads nothing of it. It is kept apart from `brief.md`: the brief says how the tools work,
the prompt says who the agent is and what it wants.

- **The file is read by the host**, not by the game: UTF-8, at most 16,000 characters; a relative
  path is read from the folder that holds the tools folder (`mods/AI`), so
  `"prompt_file": "examples/persona.md"` reads the example.
- **How it reaches the model.** It is the `prompt` of `ai_session`'s answer - the first thing an
  agent receives, whatever its application can or cannot set as a system prompt - and again of
  `ai_look { part = "prompt" }`. It is never part of a step's `now`. A whole look reminds in
  one line that it stands. An application that can set a system prompt may put the same file
  there itself; the server offers it as a tool answer only, not as an MCP prompt or resource.
- Mode and prompt are kept in a file of the copy (`Script Extender/Gwynerva_GateKitAI/session.json`)
  and outlive a load of a save, which restarts Lua.

## The plan, and why it is not a tool

A plan is a route as data (`Lua/Plans/<name>.json`): steps with a goal in words, the act that
does it, and a condition of arrival read from the game (a point reached, a height, the party's
size, a fight on or won, a window waiting, a scene's flag, the level left). `now.route` names the
next step, `do = plan` does it, and a step is passed by itself when its condition is true.

It is kept, in open mode only, as an option of `ai_session` (`plan`, `step`), and it is no
longer a tool of its own. A route with conditions read from the game is knowledge a player has
not, so blind mode refuses it; there the goals are words of the owner's prompt, which can carry
"what to do" as well as a plan can. What the plan still buys in a directed run is steps: a walk
of the route is one call with no argument to get wrong, and the agent is never asked to find a
door it was told about in prose. Lost by the change: moving along the plan by hand is now
`ai_session { step = n }` instead of calls of a tool. Gained: one tool fewer for a small model to
choose among, and a blind mode with no route in it at all.

## Notes

`note` on a step keeps a line of the agent's own with the level and the point it stood at
(`AI.Notes`), in a file of the copy, so it outlives a load, a reset and the game's restart. Notes
written within fifteen metres come back in `now.notes` when a session comes there;
`ai_look { part = "notes" }` reads the level's. They are the agent's memory, told in both modes.

## Scenes

Any table under the global `GKScene` of GateKit's Lua environment is a scene, with no name known
to the mod: its true flags, texts and numbers are its state (`ready`, `awaiting`, `stage`,
`fired`, `done` first; tables flattened two levels; `log` left out), its functions are its calls.
A scene of the client's is read once in thirty frames and listed; its calls are not reachable
from the server.

## One contract, any client

**How others do it.** The Model Context Protocol is the wire most agent applications share: its
specification defines stdio and Streamable HTTP as the standard transports[^mcp]; command-line
and IDE agents and local runners take a stdio server by its command[^clients], while hosted chat
products and some local front-ends take remote servers only and reach a stdio server through a
proxy[^remote]. Under MCP and beside it, every vendor's function calling takes a tool as a
name, a description and a JSON Schema of its arguments, and local runners expose the same as the
`tools` array of an OpenAI-compatible chat endpoint[^local]. The dialects differ where schemas get
clever: strict modes ask for `additionalProperties: false` and every field required[^strict],
others accept only a subset and strip keywords[^subset]; names are limited to letters, digits,
`_` and `-` and to 64 characters in the narrowest[^names]. Vendors also differ in where a system
prompt goes, whether several tools are called in one turn, whether a tool's answer may hold an
image, and how large the context is - a local runner may default to a few thousand
tokens[^context]. Guidance for tools an agent uses agrees on few tools with an action argument,
answers of high signal only, and errors that say how to correct the call[^tools]; a stable tool
list keeps a prompt cache valid[^cache]. No primary source read recommends a text grammar of
one's own for weak tool callers: runtimes already fall back to a text format internally, and
constrained decoding makes a call well-formed, not well-chosen[^grammar].

**What the mod does about it.**

- The contract is `tools/ai.json` and nothing else: four tools, flat objects of strings and
  integers, an enum for `do`, the keywords `type`, `properties`, `required`, `description`,
  `enum` only, names of lower-case letters, digits and underscores, every argument described.
  A unit test holds the file to this (`unit/GateKit.UnitTests/AiToolsTests.cs`). Arguments are
  checked again by the bridge, since a client may strip or ignore schema keywords.
- **MCP over stdio** is the main front: `gatekit --instance <copy> mcp --only ai_` serves the four
  tools and no other. Without `--only` the same server also lists GateKit's general tools, for
  people.
- **Without MCP**: `gatekit --instance <copy> mcp --only ai_ tools --format functions` prints the
  same four definitions as the `tools` array of a chat-completions request, and
  `gatekit --instance <copy> mcp call <tool> '<json arguments>'` runs one tool and prints its
  answer - the two commands a loop around any model needs. A client that takes only remote MCP
  reaches the stdio server through an MCP proxy of its own choosing. The toolkit has no HTTP
  server, and the mod adds none.
- Tool descriptions and the brief hold nothing that changes between calls and name no vendor.
  The brief works as a system prompt or as a first message. Nothing relies on parallel tool
  calls (a step is one act and the answers do not depend on order), on reasoning being shown, or
  on images: `now` is text.
- For weak tool callers: one closed set of acts with flat arguments, answers of one shape, a
  refusal that carries the corrected call, a route act (`do = plan`) with no argument at all.
  There is no second, text-only grammar: nothing read supports one, and the refusal with its
  hint is the correction a malformed call gets.

**Proven:** the schema's shape and the definitions given to a client without MCP (the unit
test); the same call answered alike through the MCP server, `mcp call` and
`gatekit exec server "return Mods.GateKitAI.AI.Tool('look', { part = 'party' })"`. **Argued, not
run:** any model other than the one that drove the tests; a remote-only client through a proxy;
a model with a context of a few thousand tokens.

## Choosing the model

One step costs the game's time plus the model's. The game's side is in every `ai_step` row
(`ms`, `ticks`, `chars`):

    gatekit --instance <copy> exec server "local t = {} for _, r in ipairs(GK.EventLog.Since(0, { kinds = { ai_step = true } }).rows) do t[#t + 1] = r.payload end return t"

From the start of the prologue to the end of its first fight, in open mode with the plan
`prologue`, at the working speed: 20 steps done, 4 refused, 1 look; a step took 2.0 s of the
game in the middle and 10.1 s at worst (a walk of 68 m with a dialogue starting in it); 46 s of
the game in all. A `now` block was 530 characters in the middle and 1009 at worst; the fight's
first block is the one that meets the cap.

So the run's time is `steps x (game seconds + model seconds)`, and the model's seconds dominate:
with a model that answers a step in 2 s the stretch above is about 20 x (2.3 + 2) = 86 s, with
one that takes 8 s about 20 x (2.3 + 8) = 206 s. The whole prologue is some four times that
stretch in steps.

What a step asks of the model differs:

- **Walking the route** (`do = plan`, `continue`, `wait`, `take`, `end` after acting) needs no
  judgment: the answer names the next call. The fastest model that calls tools reliably does it.
- **A fight's turn** needs a little: read `can`, choose a target, give `who`. A fast model does
  it when `can` is in front of it; it is where a weak tool caller first errs (a wrong `who`, an
  act out of turn), and each error costs one refused step, not a wrong act.
- **A dialogue's choice, an owner's remark, an improvised detour** need the character's voice and
  judgment: this is where a stronger model earns its seconds.

A fast model for the legs and the fights with a stronger one for talk - or one model at a low
effort, raised where it stumbles - follows from that; so does a local model, with the caveat that
a small one's tool calls are less reliable and its context smaller, which is what the cap, the
`brief` detail and the refusals with hints are for. Nothing about any particular model is
measured here: the numbers are the game's side only.

## Rows

Everything the agent did is a row of the family `ai`: `ai_session`, `ai_step` (the act, its
target, how it went, the line, the thought, ticks, milliseconds, the length of `now`), `ai_said`,
`ai_accent`, `ai_look`, `ai_scene`, `ai_plan`, `ai_note`. A look is a place where `now` fell
short: its rows are the list to mend.

## Rough

- The host refuses a call with an unknown or a missing required argument before the bridge sees
  it, in the host's own words, not as `{ ok = false, why_not, hint }`.
- A step that ends in a level change (the prologue's last) loses its answer: the load restarts Lua.
- Taking control of another party member (`select`, or `who` on `cast` and `attack`) is followed
  by ten ticks of waiting before the click: the client holds the newly controlled character whole
  only then.
- `near` is read from the controlled character: a change of control tells again what that
  member perceives.
- The server offers the owner's prompt as a tool answer only, not as an MCP prompt or resource.

[^mcp]: https://modelcontextprotocol.io/specification/2026-07-28/basic/transports
[^clients]: https://code.claude.com/docs/en/mcp , https://openai.github.io/openai-agents-python/mcp/ , https://code.visualstudio.com/docs/copilot/customization/mcp-servers , https://github.com/google-gemini/gemini-cli/blob/main/docs/tools/mcp-server.md , https://lmstudio.ai/docs/app/mcp
[^remote]: https://developers.openai.com/api/docs/guides/tools-connectors-mcp , https://docs.openwebui.com/features/extensibility/mcp/ , https://github.com/open-webui/mcpo
[^local]: https://docs.ollama.com/capabilities/tool-calling , https://lmstudio.ai/docs/developer/openai-compat/tools , https://github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md , https://docs.vllm.ai/en/latest/features/tool_calling.html
[^strict]: https://developers.openai.com/api/docs/guides/structured-outputs , https://platform.claude.com/docs/en/build-with-claude/structured-outputs
[^subset]: https://aistudio.google.com/docs/function-calling
[^names]: https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create , https://platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools , https://modelcontextprotocol.io/specification/2026-07-28/server/tools
[^context]: https://docs.ollama.com/context-length , https://platform.claude.com/docs/en/agents-and-tools/tool-use/parallel-tool-use
[^tools]: https://www.anthropic.com/engineering/writing-tools-for-agents , https://developers.openai.com/api/docs/guides/function-calling
[^cache]: https://platform.claude.com/docs/en/build-with-claude/prompt-caching , https://developers.openai.com/api/docs/guides/prompt-caching
[^grammar]: https://lmstudio.ai/docs/developer/openai-compat/tools , https://arxiv.org/abs/2605.26128
