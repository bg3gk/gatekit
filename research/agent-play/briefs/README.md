# Agent briefs

What an agent reads before it plays the game through `stand/situation.py` (the honest situation
API, docs/situation.md). `stand/agent_run.py` puts a brief together and prepares a run folder.

A brief is made of independent parts, and none of them names a place:

| Part | Where | What |
|---|---|---|
| wrapper | `wrapper.md` | the reusable context every run gets: the interface, memory and saves, the rules of play |
| task | `tasks/*.md`, `--task` | what to do: `prologue` (escape the place you wake up in), `explore` (play on from wherever the game is). Add your own |
| addons | `addons/*.md`, `--addon` | how to play or what else to do, for any task: `tester` (wishes file, log, report), `newcomer` (no memorised game knowledge), `immersive` (role-play lines; from agent-immersive.md) |
| prompts | `--prompt`, `--prompt-file` | anything more, in the order given: a backstory, a temperament, a play style, a modder's technical task (`examples/`) |
| start | `--save LABEL` | where the run starts: a save of `situation.py save` (or a game save's folder name), loaded before the run; without it, the game as it is |

Placeholders any part may use: `{{play}}` (the run's command), `{{wishes}}` (its wishes file),
`{{limit}}` (`--limit`, default 250 commands), `{{until}}` (`--until`, default "you are stuck with
no idea left"); the wrapper also has `{{access}}` and `{{tasks}}`.

```
python stand/agent_run.py --task prologue --addon newcomer --addon tester
python stand/agent_run.py --task explore --save "Before the jar" --addon tester --limit 80
python stand/agent_run.py --task explore --immersive --prompt-file stand/agent/examples/character.md
python stand/agent_run.py --task explore --prompt "Play as a coward who avoids every fight."
python stand/agent_run.py --prompt-file stand/agent/examples/modder-test.md --save camp   # the prompt is the task
python stand/agent_run.py --task prologue --print                                        # show the brief only
```

So a test run with wishes is `--addon tester` on any task from any save: the location is chosen
by the save, the goal by the task, the testing by the addon. Additional prompts can change the
task; the rules of play stay (and the interface enforces them anyway).

A run folder (`%LOCALAPPDATA%/GateKit/agent-runs/run-<time>/`, or `--dir`) holds `BRIEF.md`,
`play.sh` / `play.cmd` (the only command the agent uses: `stand/agent_play.py`, strict mode, a
player's commands only), `wishes.md` and `run.json` (the parts, the start). The run's notes are
its own (`GK_AGENT_STATE` = the folder's name; a start from a save leaves a note saying so); saves
belong to the game copy and are shared by its runs.

Hand the brief to any agent that can run a shell command ("Read <run>/BRIEF.md and follow it"),
or add `--run` to have the `claude` CLI play it headless with only the play command and the
wishes file allowed (`--open` adds the web; `--model` picks the model); the final answer lands in
`report.md`. `--run` is written for the CLI's `-p` / `--allowedTools` and has not been tried on
the machine it was written on (no CLI there).
