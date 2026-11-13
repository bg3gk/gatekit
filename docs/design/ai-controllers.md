# AI controllers: who plays which characters

**Status: design, not built.** The bridge protocol already carries what it needs
(`docs/contracts/bridge-protocol.md`, the question channel); the rest of this page is the plan
the implementation must follow, and the reason the protocol looks the way it does.

Two things people have asked for turn out to be the same feature:

1. **An AI companion.** One member of the party is played by an agent instead of by you. It is
   marked in the party list, it plays only in combat, and in a shared initiative it moves after
   you have finished with all of your own characters.
2. **AI opponents.** Every unit no player controls is played by an agent instead of by the
   engine's own tactics, for a harder and less predictable fight.

Both are "somebody outside the game decides this character's turn". So there is one mechanism —
a **controller** — and the two features are two ways of assigning it. There may be several
controllers in one game at once, each its own mind with its own colour.

## What a controller is

A named mind with a colour, an agent behind it, and the characters it plays:

```lua
GK.Control.Add(name, options)       -- a controller: agent, colour, prompt, when
GK.Control.Assign(name, scope)      -- scope: a character, "party", "enemies", "npcs", a faction
GK.Control.Release(scope)
GK.Control.Remove(name)
GK.Control.List()                   -- controllers, what each plays, and who is deciding right now
```

```lua
-- an AI companion
GK.Control.Add("astarion", { agent = "claude", colour = "violet", when = "combat",
  prompt = "You are Astarion. Prefer the shadows and the flank; mock the others when it costs nothing." })
GK.Control.Assign("astarion", astarionUuid)

-- every unit no player controls, played by a second mind
GK.Control.Add("foe", { agent = "claude", colour = "crimson", when = "combat",
  prompt = "Play to win, but in character." })
GK.Control.Assign("foe", "npcs")
```

- **`when`** is `"combat"` for now, and only that. Outside combat an assigned companion belongs to
  the player again: it walks with the party, talks, loots. Nobody has asked for an agent that
  wanders the world on its own while you play, and it would be a different, harder thing.
- **`agent`** names a host-side runner, not a model. What a runner is, the host decides
  (`gatekit serve --agent <name>`); the game only knows the name to show. Two controllers may
  share one agent or use different ones, and a question carries the controller's name so the host
  knows which mind is being asked.
- **`colour`** is how a player tells one mind from another; see below.
- **Everything a controlled character owns is controlled with it**: summons, familiars, wild
  shapes. They are resolved at the moment of the turn from ownership, not stored, so a summon
  conjured mid-fight is covered without anyone registering it.

Assignments live in the save (`PersistentVars`), so a companion stays an AI companion across a
reload. `GK.Control.Release` and a toggle in the overlay take it back.

## How a turn happens

```
 combat: it is this character's turn
        │  the character is assigned, and a host is listening (serve_server.json)
        ▼
 the mod writes ask_server_<id>.json   kind "turn"
        │     payload: who, what it sees, what it may do, how long it has
        ▼
 the host runs the agent, answers tell_server_<id>.json
        │     result: a list of actions, or { pass = true }
        ▼
 the mod executes them through GK.Act, one at a time, checking each
        │
        ▼  anything refused, malformed, or past the deadline
 fall back: let the engine play the turn as it always would
```

The game never waits on a thread and never hangs: the deadline is part of the question, and a
host that stops answering is noticed within three seconds.

**The actions come back as data, not as Lua.** The agent asks for `{"act":"Cast","spell":"...",
"target":"..."}`; the mod decides whether that character really could do that, and does it
through `GK.Act` — the same honest, player-equivalent layer the agent-play work already uses.
An agent cannot teleport, cannot read what the character cannot see, and cannot spend what it
does not have, whatever it answers. Cheats stay in `GK.Cheat`, which no controller may call.

### Order within a shared initiative

Baldur's Gate 3 gives allies one shared turn in which you move your characters in any order. An
AI companion must not take that turn away from you, so:

- While any character **you** control in that initiative group can still act, the companion
  waits. "Can still act" is `GK.Act.HasEnded` and `GK.Act.CannotAct` — ended its turn, or has
  nothing left to do with.
- When they are all finished, the companion's turn begins and the question goes out.
- If you end the whole group's turn before it moves, it loses the turn, exactly as one of your
  own characters would. The overlay says so rather than silently skipping.

For enemies the question is simpler: each has its own turn, and the question goes out when that
turn starts.

## What the player sees

Every controller has a colour, and that one colour says everything about it in three places:

1. **The portrait.** A haze of it on the inner edge of the party portrait of each character that
   controller plays, drawn by the client over the game's own portrait. While the agent is
   thinking the haze pulses. If the portrait's position cannot be found reliably in a given
   interface state, the fallback is a small badge in the corner rather than a haze in the wrong
   place.
2. **The thought.** The line the overlay shows of what it decided — "Astarion: hides, then shoots
   the goblin on the ledge" — is written in that colour, with the controller's name. A fight
   where you cannot tell why your companion did something is not fun; the reasoning the agent
   returns is shown, not hidden.
3. **The targets in the world.** While the turn plays out, whatever it is acting on — whom it
   attacks, where it moves, what it picks up — is outlined in the same colour in the world, so
   you can follow a turn you did not decide without reading anything.

With several controllers in one fight, an exchange reads at a glance: a violet companion flanks,
a crimson enemy answers, each thought in its own colour, each target outlined in the colour of
whoever chose it.

**Colours** are named from a small palette that stays apart on screen and stays apart for the
colour-blind (violet, crimson, amber, teal, jade, rose), or given as hex. A controller with no
colour takes the next free one in that order; two controllers may not take the same colour, and
`GK.Control.Add` refuses rather than quietly drawing two minds alike. The palette is shared with
Weave's themes, so a mod's own window can use a controller's colour and match.

**Outlining in the world is the uncertain part.** The engine highlights entities for its own
reasons (hover, selection), and whether a mod can colour that highlight per entity, and have
several colours on screen at once, is not yet known — the Script Extender coverage work is the
place to answer it. The fallback, if the engine will not give its highlight away, is a marker
drawn above the target in the controller's colour. This is the one piece of the design that may
have to change shape.

## The instruction the agent gets

`GK.Control.Brief(who)` builds it, and the host sends it:

1. **Who you are** — the character, their class, what they carry, what they can do this turn.
2. **What you see** — `GK.See`, trimmed to what that character could actually perceive.
3. **What you may do** — the legal actions with their costs, from `GK.Act`.
4. **How to answer** — the action format, and that anything else is refused.
5. **The extra prompt** — the `prompt` of the assignment, verbatim, last, so it colours the
   decision without being able to remove the rules above it.

A global default prompt is settable (`GK.Control.Defaults`), and a mod may add its own brief for
its own characters.

## What has to be built

| | |
|---|---|
| protocol | the question channel — **in the contract, not yet implemented on either side** |
| host | `gatekit serve`: answer `turn` questions, run a named agent, a time limit, a log of every decision |
| mod, server | `GK.Control.*` (several controllers, each with a colour), ownership expansion, turn detection, the shared-initiative rule, executing answers through `GK.Act`, reactions (Ask and Automate), the fallback |
| mod, client | the portrait haze per controller colour, the thinking pulse, the decision line, outlining the turn's targets in the world, a toggle per party member |
| offline | nothing new |

Open questions worth settling before writing it:

- **Can the engine's own outline be coloured per entity, several at once?** If not, markers.
- **Should a controller speak?** A bark in combat, in its colour, would do a lot for the feeling
  of a second mind at the table — but it needs the voice-less dialogue path, not a line of text
  over a head.
- **Multiplayer.** "The player's units" there means several players' units; the rule above should
  be read as "all human-controlled units in that group", and that is how it should be built, but
  it has never been tried.
- **Two controllers in one initiative group** — a violet companion and an amber one. They should
  move in the order the engine gives, each asked in its turn, never both at once; the mod must
  serialize the questions even though the host could answer them in parallel.

## Reactions

A character's turn is not the only moment it decides. Baldur's Gate 3 asks about **reactions** —
an opportunity attack, a shield spell, a counterspell — either by popping a window mid-combat
("Ask") or by resolving them from rules you set in advance ("Automate", with its conditions).

A controller must handle both, and this has not been tried:

- **Set the rules.** A controller should be able to put its character's reactions into Ask or
  Automate, and set an automated reaction's conditions, as a player does in the character sheet.
  Which of this the engine exposes is unknown; `GK.Gui` reads and drives the game's own windows,
  so the reaction settings are reachable at worst through the interface the player uses.
- **Answer the question.** When the window appears during a fight — including during *another*
  character's turn, which is when most reactions are asked — the controller whose character is
  asked decides and confirms, within the deadline, and the overlay shows what it chose in that
  controller's colour like any other decision.
- **Do not answer for a player.** A reaction window belonging to a character a person controls
  is never touched, even while an agent is mid-turn elsewhere.

Until this is proven in a live fight, a controller should default its characters' reactions to
Automate, so an unanswered window can never stall a fight. Proving it means: a goblin walks past
the AI companion and the opportunity attack is taken; a spell is cast at it and the shield goes
up; and a reaction window raised while the player is acting is answered without disturbing them.
