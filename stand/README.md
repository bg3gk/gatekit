# The video stand

A rough stand for one video: an AI agent plays the prologue by itself, and a watcher can follow
what it is about to do. It lives on the branch `video-stand`, cut from `rolls-real`, and is not
meant for `main`: what proves universal here goes back into the toolkit separately, reviewed, and
is listed for the owner when it does. Rough is allowed here; breaking the game copy's own rules
(copies only, the window in the background, no real mouse or keyboard) is not.

## Who does what

- **The player is an AI agent** in an AI application (Claude Desktop or another), driving one copy
  of the game through GateKit's MCP server (`gatekit --instance <copy> mcp`), as the blind runs
  did. The owner talks to that agent by voice or text; the agent decides and acts.
- **The stand gives the agent three things**: a fast way to read where it stands (one call, by
  layers), a way to show its intent in the game (a line over the head, a purple accent, the
  game's own camera focus and target sound), and sub-scenarios that fire by the situation.
- **The stand does not talk and does not listen.** Voice, replies and commands are the
  application's; nothing of sound goes into the game.

## The layers (what the agent reads, one call)

`GK.Stand.Feed(since)` - also the MCP tool `bg3_stand_feed` - answers what changed since the
cursor, grouped by layer, each layer a short list of plain rows:

| Layer | What it holds | From |
|---|---|---|
| `situation` | the place, the party with hit points and what is in hand, the quest's objective and its marker, whether a fight is on and whose turn | `GK.See.Status`, `GK.See.Combat` |
| `seen` | who and what the party perceives now: creatures (hostile or not, distance, direction), doors and ways on, things to use | `GK.See.Around`, `GK.See.Ways` |
| `heard` | lines spoken since the cursor, who said them, where | `GK.Heard` |
| `waiting` | what waits for the player: a dialogue with its answers, a roll, a reaction, a loot window | attention |
| `about_to` | what each creature is about to do and what it weighed, with the game's own chance | the rows `fight_options`, `fight_action` |
| `dice` | every roll: who, what for, the dice, the result, what an interrupt changed | the rows `dice_thrown`, `dice_adjusted` |
| `happened` | hits with their damage and what resisted, heals, deaths with who killed, who joined and left the fight | the game's rows and the family `fight` |
| `said` | the agent's own lines and reasons, in order | `GK.Stand.Say` |
| `scenario` | which sub-scenario is armed, which fired, what it is waiting for | the stand |

The agent's own three voices are one call, `GK.Stand.Say{ think, say, do, at }` (MCP:
`bg3_stand_say`): `think` is what the character thinks (kept in the feed only), `say` is the
short line shown over the character's head, in the first person, of what it is about to do,
`do` is the plain reason for a reader of the log, `at` is what the line is about. With `at` the
stand marks the thing: the purple accent, the game's camera focus, the target sound.

## The accent (what the watcher sees)

One verb, `GK.Stand.Accent(at, options)`: the purple outline and ring the toolkit has
(`GK.Focus`), the game's own camera focus moved to the thing and back, and the game's target
sound. The camera part and the line over the head are built in the toolkit proper, on
`rolls-real`, as universal verbs with tests; the stand only calls them.

## Sub-scenarios

A sub-scenario is a small table: `when` (a condition over the feed: a place reached, a creature
seen, a fight ended, an action asked), `run` (steps of staged events), `once`. They are armed at
the start and fire by the situation, never by a script of the level:

- **Watchers.** Mizora and Raphael stand somewhere on the level, each out of the way. When the
  party comes within a few metres of one, it vanishes with its own effect.
- **The queen's answer.** After the first fight, when the agent shoves Lae'zel off (the shove is
  made to succeed), Vlaakith appears beside the character, says her line and casts Power Word
  Kill. The stand then loads the save made just before the shove.
- **Skill issue.** When Command: Drop is cast at Zhalk, his save is made to succeed, he says his
  line, and Shadowheart polymorphs him.

Each uses the toolkit's own instruments: a roll decided at the throw (`GK.Rolls.Rig` by spell),
the stage's spawn and status verbs, a checkpoint and a reset.

## Stages of the work

1. Base mechanics: the camera focus and the line over the head (toolkit); `Say`, `Accent`,
   `Feed` (stand).
2. The prologue: the agent's brief, the copy started in the prologue from the owner's save, the
   speed, a first whole run.
3. The sub-scenarios and whatever else is fun and fits the lore.
