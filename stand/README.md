# The video stand

A rough stand for one video: an AI agent plays the prologue by itself, and a watcher can follow
what it is about to do. It lives on the branch `video-stand`, cut from `rolls-real`, and is not
meant for `main`: what proves universal here goes back into the toolkit separately, reviewed, and
is listed for the owner when it does. Rough is allowed here; breaking the game copy's own rules
(copies only, the window in the background, no real mouse or keyboard) is not.

## Who does what

- **The player is an AI agent** in an AI application of the owner's choice, driving one copy of
  the game through the bridge of the mod GateKit AI. The owner talks to that agent by voice or
  text; the agent decides and acts.
- **The bridge is a mod of its own**, `mods/AI` ([mods/AI/README.md](../mods/AI/README.md)): four
  tools, each answer carrying what changed in the game since the agent's last call. It shows the
  agent's intent in the game (a line over the head, a glow on the target, the camera's focus), and
  reads the state of the stand's scenes with no name of them known to it.
- **The stand is the scenes** (`stand/scenes`, `stand/menu`, `stand/creation`, `stand/splash`:
  [scenes/README.md](scenes/README.md)) and the owner's prompt for this run
  ([prompt.md](prompt.md)). Scenes are scripts, armed by the driver, and fire by the situation.
- **Nothing here talks or listens.** Voice, replies and commands are the application's; nothing
  of sound goes into the game.

## Setting a session up

1. **Start the copy** in the prologue and set the working speed:

       gatekit --instance <copy> up --level GustavX:TUT_Avernus_C
       gatekit --instance <copy> exec server "return GK.Speed.Set('working')"

2. **Arm the scenes** of the prologue (again after every `reset`: a load restarts Lua):
   [scenes/README.md](scenes/README.md), "The driver's order of the whole video".
3. **Give the application the server.** `gatekit --instance <copy> mcp config` prints the
   registration; add `--only` and `ai_` to its arguments, so that the server line is

       gatekit --instance <copy> mcp --only ai_

   and the agent's tool list is the four tools `ai_session`, `ai_step`, `ai_look`, `ai_scene` and
   nothing else. An application without MCP is given the same tools with
   `gatekit --instance <copy> mcp --only ai_ tools --format functions` and calls them with
   `gatekit --instance <copy> mcp call <tool> '<json>'`.
4. **Give the agent the brief**: the text of [mods/AI/brief.md](../mods/AI/brief.md), as the
   application's system prompt or pasted as the first message.
5. **Begin the session**: tell the agent to call

       ai_session { "mode": "open", "plan": "prologue", "prompt_file": "../../stand/prompt.md" }

   Its answer hands the agent the stand's prompt and the whole situation. `"mode": "blind"`
   instead, with no plan, is the run in which the agent knows only what a player could: the
   scenes still fire and the director commands still work, but the agent is told nothing of a
   scene's state, so the owner says when a moment is ready.
6. **Play.** The owner talks; the agent steps.

## Director commands

What the owner says and which call each command means is the table of [prompt.md](prompt.md),
"Director commands": the watchers, the shove and the queen's answer, skill issue, the finale.
The driver's side of each - arming, the checkpoint before the shove, the reset after it - is the
table of [scenes/README.md](scenes/README.md).

**The run is over** when `finale` says `done`: the agent stops, and the driver does nothing more.

## Reading a run afterwards

Every step, line, thought, look and scene call of the agent is a row of the family `ai` in the
copy's event log; [mods/AI/README.md](../mods/AI/README.md), "Rows" and "Choosing the model",
has the command that reads them.
