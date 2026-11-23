# Rules for the API

What anything added to the API - a verb inside the game, a tool, a command, a fixture, a
schema - is held to. A *verb* is one function of the API inside the game: `GK.Gui.UseSlot`,
`GK.Stage.Spawn`.

## Universal, never a special case

A piece of the API holds across the whole game: any character, any spell, any window, any
venue it can meaningfully meet. It is driven by the game's own data and state, not by a list of
known cases. A piece that works for one scene only is generalised or stays inside the test that
needed it. Every new piece says what it covers and what it does not, and is tried on two unlike
cases before it is merged. Universality is asked of the API, not of a test's subject.

## Every piece of work reviews the API

Covering a mechanic, writing a test, running a case - each ends with a short section on the API
it used: what to improve, remove, make faster, cover additionally. A general fix that is small
is made in the same piece of work; the rest become the open items
([research/open-items.md](../research/open-items.md)). The API is expected to
get smaller and faster as well as larger: a verb nobody needs, or two that do the same, goes.

## Events, not waits

Anything that takes time is followed by an event, not by a pause. A verb that starts something
returns when it has seen the thing happen, and says what it saw; a test moves on when the row
it waits for arrives in the event log. A sleep of a fixed length and a timeout counted in real
seconds are defects: they make a test slow at speed 1, wrong at speed 8, and blind to what
really happened.

**A wait is a smell.** Every wait, pause, poll or retry - "wait N ticks, check, wait again", "no
change for N ticks", "try again in a moment" - is treated as a defect to be removed, in the API, in
tests, in tools and in an agent's own work, unless the wait itself is what is being tested. The
model is always: the game acts, an event says so, the caller reacts. Before a wait is written, the
event is searched for with every instrument at hand (the event log's kinds, attention, the game's
own events and states, the start's timeline, watches, probes, coverage); a wait that is kept after
that search is written down as a gap with what was tried, and replaced when the event is found.

- Whatever a caller may need to wait for has a row of its own in the log - a state that begins
  and ends, a window that opens and closes, an action that is carried out or refused, a roll
  that is made. Report success once the requested action is observed. Report a refusal or a dropped
  request with the game's condition when it is known. Where the game gives no event, look for one
  or report the state change as a row, and record the gap.
- What by the game's own rules and logic comes to an end, ends with an event: the game has to
  tell its own systems, its interface and its story that the thing is over. Not having found
  that event is not the same as there being none. Before a read on the tick is written, the
  event is looked for with the method's instruments - what the game raises around the moment
  (the tracer over the step), what writes the state that changes (the memory instrument), what
  the mods and Script Extender already listen to - and a read on the tick is the last resort,
  recorded as a gap with what was tried, so that it can be replaced when the event is found.
- Where the event comes from, in order of preference: the game's own event (an Osiris event,
  a one-frame component, a subscription to a component being created or destroyed), which
  costs nothing while nothing happens; then a state read on the tick. A read on the tick
  exists only while somebody waits for it or a scope has switched its family on, and ends with
  that wait or scope: nothing polls for a listener that is not there. All such reads run from
  one place on the tick, are counted and timed per family (`GK.EventLog.Costs`), and a family
  whose cost shows in the tick is a defect to fix, not a price to accept. No verb keeps a
  timer or a tick hook of its own.
- A limit stays as a guard against a hang, counted in game time or ticks, generous, and
  reported as "the event never came" - never as the ordinary way a step ends.
  Check whether the game failed, declined the request or emitted a different event.
- A test that fails only when fast is first a suspicion that something waits on the clock.

## Fair, cheat, observation

Every verb has one of three classes. These are the words for them: an action the player could
take is `fair`, never "honest".

| Class | Means | Lives in |
|---|---|---|
| `fair` | What a player could do through the game itself: it goes the player's path, rolls, pays its costs and passes the game's own checks. | `GK.Act`, `GK.Gui` |
| `cheat` | Sets the world directly. | `GK.Cheat`, and the namespaces that prepare a scene: `GK.Stage`, `GK.Fixture`, `GK.Rolls`, `GK.Speed`, writing through `GK.Memory` |
| `observation` | Reads and changes nothing. | Beside what it reads |

### The namespace decides the class

A name that reads as fair is fair. Nothing in `GK.Act` or `GK.Gui` skips a roll, a cost or a
check; a verb that does is not allowed there. Where a thing has both forms they carry the same
name, each in its own namespace: `GK.Act.Attack` attacks as the player does, `GK.Cheat.Attack`
orders the attack by script.

The class of every verb is data in one registry. A unit test holds the registry to the code
both ways - no verb without a class, no class without a verb - and each namespace to its class.
The MCP tool `bg3_api` lists the verbs with their class.

A fair verb never falls back to a cheat. Where the player's path is not built yet, it answers
that it cannot, and names the fair route and the cheat.

### Why the class comes first in the name

- **It can be withheld.** A whole namespace can be kept from a client, where a flag would have
  each call inspected. Inside a test this is enforced: a fair step that calls a cheat fails.
- **A test reads true.** A line begins with what it is, so preparing a scene and acting in it
  cannot be mistaken for each other.
- **It is checked by a search.** Every cheat a test makes is found by looking for one prefix.
- **Completion offers only what is fair** under `GK.Act` and `GK.Gui`.

A flag on a fair verb (`{force = true}`) is not used: it is a cheat under a fair name.

The same split is made by test libraries that separate acting as a user from dispatching an
event directly, and by game engines that keep cheats in a manager of their own.

### What a fair client may know

Knowledge has the same split. Every kind of event declares whether a player could know it
through the game (`player`) or not (`omniscient`). The host gives a fair client `player` rows
only; the client is not trusted to look away.

### In a test

A step declares its class. A fair step that calls a cheat fails, and so does an observation
step that calls anything else. A test's header says how its scene was made. A pinned roll, a
staged creature and a changed speed are cheats and are recorded as such in the event log.
