# Mapping the engine by playing it

**Status: design, not built.** It replaces the way coverage was done before.

Until now the engine was mapped by **tours**: a recorded sequence of things to do in the game,
replayed to see which of its components appeared. It worked, and it has two faults that no
amount of more tours fixes. A tour covers what somebody thought to put in it, and the engine's
components are not a list anybody wrote. And a tour is blind: it drives past a component that
has just appeared for the first time without noticing, because noticing was never its job.

The pieces to do better did not exist when the tours were written. Now they do: honest
player-equivalent actions, a perception layer, a decompiler and a symbol database, several games
at once in one window, and frame caps that make several affordable.

## The shape

Two agents, each driving one or two copies of the game through GateKit's API. Mostly they
**play** — the honest actions a player has, because what a player can reach is what a modder
will meet. Where a situation will not occur by playing, they **reach for a cheat** to stage it:
a status, a surface, a death, a level-up.

What makes it different from a tour is the loop. The game is watched while it is driven, and
when a component or a system **comes alive for the first time**, the agent stops going forward
and starts closing in:

- **do it again**, to see whether it was the action or a coincidence;
- **vary it** — the same action on another target, in another state, with another weapon — to
  find which part of the situation the component belongs to;
- **force a value**: if a field is suspected to mean "the attack was from behind", arrange an
  attack from behind and one from the front, and read both;
- **ask the decompiler** when the game will not say: read the code that writes the field, then
  design the action that proves the reading.

That is the whole point. The old loop produced *confidence*; this one produces *evidence*.

## The amendment that makes it evidence

An improvising agent is not reproducible, and a finding nobody else can reach again is an
anecdote however true it is.

So **tours do not disappear — they change ends.** They stop being the input and become the
output: every confirmed finding leaves behind the shortest sequence that makes the component
appear, as a scenario that can be replayed. A run's product is not only "this component exists
and this field means that", it is "and here is how to see it again in thirty seconds".

Those recipes are what let the next person check us, what turn into regression tests when the
game is patched, and what make a claim worth sending to the Script Extender's author.

## Rules a run holds itself to

- **A sighting is not a finding.** One appearance is a lead. A claim needs the action repeated,
  or the value forced, or the code read — and the record says which.
- **How it was reached is part of the record.** A component that appears in ordinary play is a
  stronger fact than one that only ever appears after a cheat; both are worth having, and they
  are not worth the same.
- **The unseen pulls.** An agent will chase what is interesting and leave the dull parts of the
  engine for ever. The ledger of what has never been seen is what decides where a run goes next,
  not the agent's curiosity.
- **Say "I do not know".** An unresolved field stays unresolved in the record. The coverage work
  has been bitten before by a confident wrong name, and a wrong name is worse than a missing one
  because it travels into other people's mods.

## What it needs

Most of it exists. The missing parts are the ledger of the unseen in a form an agent can ask
about, the recipe recorder, and a way for a run to report its own cost — agents driving games
for hours are not free, and a run that cannot say what it spent cannot be judged.
