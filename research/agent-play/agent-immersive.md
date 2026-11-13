# Immersive mode (an optional addition to an agent's brief)

Not a permanent mode: add this file's "For the agent" part to an agent's brief when a run should
be watchable as a story - for a stream, a demo, or simply to read later what the agent thought.
It changes nothing in what the agent may do; it adds what it says.

## For the agent

You play your party's characters in character. Before each group of actions (a walk with a
purpose, a fight turn, a conversation, looting a room), say two things with one command:

    say "<one line in character, as your character would say or think it>" why="<one short plain sentence: what you are doing and why>"

- The line is spoken by the character acting now (the selected one), in the voice of that
  character - their temperament, their stakes, what they know. Keep it to one or two sentences.
- `why` is plain and practical: the goal, the plan, the risk ("Walking west: the journal marker
  points there and the corridor is the only way on").
- One `say` per group of actions, not per command. Fights: one per turn of a character.
- Stay in the world: no talk of interfaces, commands or ids in the line (they may appear in `why`).
- Your choices in dialogue may follow the character's personality rather than pure advantage;
  say so in `why` when you do.
- When the line is about something in sight (an enemy, a door, a strange object), add `at=<id>`:
  a ring marks it while the line is on screen. Your commands mark their own aims already.

The line shows in the game as a notification (whoever watches the screen sees it), and both the
line and the reason go to the run's narration log.

## For the person running it

- `python stand/agent_run.py --task prologue --immersive` adds the part above to a run's brief;
  a character to play goes in with `--prompt-file` (see `stand/agent/examples/character.md`).
- Command: `stand/situation.py say "<line>" why="<reason>" [who=<id>] [at=<id>]`; MCP: `bg3_agent_say`.
- The visual focus (`GK.Focus`, docs/situation.md) works with or without this mode: rings on what
  commands aim at and what the agent examines, and a quiet game pointer while it plays in the
  background. Together with the lines it lets a watcher follow both what the agent does and why.
- Bridge: `GK.Act.Say(text, why, who)` - the in-game notification (`Osi.ShowNotification` takes plain
  text) and `GK.Act.Narration` (the log in the game's Lua state); `stand/situation.py narration`
  prints it, and every line is also appended to `%LOCALAPPDATA%/GateKit/agent-narration-<state>.md`.
- The mode costs about one command per group of actions; with `speed auto` the notification stays
  on screen for a few seconds of real time.
