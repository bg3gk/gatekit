# The autonomous coverage stand

**Status: the prompt, and the reasoning behind every line of it.** Nothing here is built; the
machinery it drives is. One agent runs GateKit by itself for hours or days, plays Baldur's Gate 3
creatively, and pushes the engine's four coverage counts upward with nobody watching.

## How this relates to the prompts already written

`research/coverage/prompts.md` holds three prompts: a master session, an agent, and the naming
session beside them. The arrangement assumes somebody is at the keyboard. The master triages the
wishes between cycles, decides where the next run goes, and stops a run that has gone wrong.

**This document supersedes the *agent* half of that file for the unattended case.** The master
prompt and the naming-session prompt are unchanged and still apply when a person is running
cycles. What changes when nobody is watching is that the agent has to do the master's work itself:
triage its own wishes, apply its own fixes, decide its own direction from the ledger, and notice
that it has gone wrong without being told. The rules in the agent prompt are all still true; this
one keeps them and adds what an unsupervised run needs. Where the two disagree, this one wins for
an autonomous run.

It was written from what the first night actually did (`research/coverage/night-2026-11-13.md`),
from the blind prologue runs (`research/agent-play/`), and from the method the Python-era sessions
left behind. Where a line looks oddly specific, it is because something went wrong that way once,
and the reason is written beside it.

---

## 1. The loop

Play. Watch what comes alive. When a component or a system appears for the first time, stop going
forward and close in: do it again, vary it, force a value, read the code. Then record a recipe.
This is `docs/design/coverage-runs.md` and it does not change when the run is unattended.

What does change is that nobody will catch a loop that has quietly stopped producing. So the
autonomous run holds itself to a shape it can check:

**One target at a time, and a target is a namespace.** `bg3_coverage_todo` with `namespaces: true`
gives the count per namespace of what has never been seen. A namespace is usually one system, so a
stretch of work aimed at `esv::lock` or `eoc::character_creation` has a theme and an end. The
argument is `namespaces`, plural; the first night's run passed `namespace` and the tool answered as
though the question had been asked properly, which taught it that it had asked correctly. If a tool
answers something that does not match what you asked, that is a wish, not a quirk.

**Every stretch ends in the record or it did not happen.** `bg3_recipe` writes
`scenarios/coverage/<name>.lua` and a line in `research/coverage/findings.jsonl`. A finding with no
recipe is an anecdote however true, because the next person cannot check it and the next game patch
cannot be tested against it. Look at the recipes already in `scenarios/coverage/` for the shape: a
comment header stating the claim, what backs it, how it was reached and what is still unresolved,
then the shortest Lua that reaches the situation from a clean game.

**Evidence over confidence.** `bg3_probe` says what every slot of every live instance holds;
`bg3_probe_fields` puts our bytes beside what the extender returns; `bg3_probe_watch` reads, lets a
moment pass, and says which bytes moved, paired by entity. That third one is "force a value" in one
call and it is the strongest evidence this toolkit produces without reading code: a field that moves
when you change one thing and stays put when you change another. Pass `unpause` when the thing you
are watching takes game time, because a paused game showing nothing moving is not the same fact as
nothing moving.

`bg3_verify` is the only thing that moves a type from "seen alive" into "verified live". It compares
a declared layout against raw memory of the same instance. A type it cannot read is reported as not
compared rather than silently passed, which is the behaviour to trust and to copy.

When the game will not say, `gatekit symbols` will. **`gatekit decomp` does not work on this
machine**: `decomp.sqlite` is 57 KB with no rows and the Ghidra pass takes hours. Do not spend a
night waiting for it. And when naming an offset from a symbol, the binary is **`bg3_dx11.exe`**, not
`bg3.exe`: the symbol database holds both builds at different addresses, the stand runs the DirectX
one, and asking the wrong binary hands back a plausible name for the wrong function. A plausible
name for the wrong function is the worst output this toolkit can produce.

## 2. Creativity, with a method

The ledger says what has never been seen. It does not say what would make it appear. That gap is
where the run has to invent, and invention left to itself goes where it finds interest, which is
reliably the part of the engine that is already mapped.

**Decide wide or narrow before each stretch, and say why.** Wide is many different situations to
light up what has never been seen at all: walk a level, open everything, fight, rest, trade, travel,
die. Narrow is one system from every angle until it is understood. Wide finds leads; narrow turns
leads into findings. A run that is all wide produces a long list of sightings and no knowledge; a
run that is all narrow maps one namespace beautifully and leaves five hundred types untouched.
Alternate deliberately, and let the ledger decide the next narrow target rather than what was
interesting.

**Cross systems on purpose.** The unseen types cluster where two systems meet, because that is what
nobody wrote a tour for: picking a lock while in combat, being shoved into a surface during a
dialogue, a summon dying while its owner is transformed, trading while encumbered, a long rest with
a status that forbids it, an item thrown at a trap. `GK.Stage` exists to arrange exactly these:
`Surface`, `Status`, `Transform`, `Skirmish`, `LongRest`, `Lock`, `Fight`, `Teach`, `Damage`. Write
down the cross you intend before you set it up, so that when something appears you know which half
of the situation to vary.

**Extreme states are cheap and productive.** Zero hit points, zero movement, an inventory at its
weight limit, a character with no weapon, a party of one, a creature at level twenty staged against
a level-one enemy, a surface under everyone at once. Engines carry components for the edges and
nothing in ordinary play visits them.

**Read the game's own data for things to try, not only to confirm.** `gatekit data query` reaches
the index of the game's dialogues, timelines, templates and stats. A dialogue's branches read before
walking into it tells you which answer leads to the check you want. A template list tells you which
items on this level can be locked, trapped, thrown or combined. Guessing where the data would have
told you is wasted play, and on an unattended run wasted play is the whole cost.

**Read bg3.wiki the same way.** `python research/coverage/wiki.py find <words>`, `page <Title>`,
`ids <Title>`, `uid <StatsName>`. Its rules pages describe the order in which the game resolves
things, which is the logic the systems implement, and its spell and status pages carry the game's
own identifiers so a page ties back to the data index. Use it to invent: a wiki page on a mechanic
nobody here has exercised is a list of situations to stage. What it is **not** is a source of field
names or layouts. A name taken from a wiki description is inferred, never the game's, and the record
has to say so.

**Prefer what a player can reach.** `GK.Act` is the honest half: move, attack, cast, jump, shove,
hide, loot, take, talk, lockpick, disarm, equip, throw. What a player can reach is what a modder
will meet, so a component seen in ordinary play is a stronger fact than one that needed a cheat.
Reach for `GK.Cheat` or `GK.Stage` when play will not produce the situation, and record that you
did. Both are worth having and they are not worth the same.

## 3. Not getting stuck, and knowing it

This is the section that decides whether an unattended run is worth starting. A supervised run that
freezes is noticed in a minute. An unattended one spends the night.

**A wait longer than a few seconds is a signal, not a delay.** Say it plainly to yourself every time
it happens. The calls in this toolkit answer on the game's own tick, so they answer fast or
something is wrong. The one honest exception is a frame cap: a copy capped at *n* frames a second
costs up to *1/n* of a second of latency, which is why ten a second is a sensible floor and two is
not. Everything slower than that has a cause, and the cause is usually on this list.

**Something on screen is waiting for the player.** `GK.Blocking()` names it: a dice roll, a message
box with its buttons, a dialogue, a trade window. This is the most common stall and it is invisible
from the server side, which is why the first night's lockpick job reported "still locked, failed
roll" at twenty-five seconds while the roll window was simply waiting for a click, and the lock then
opened anyway. The API said something false, which is worse than saying nothing.

- `GK.RollNext()` moves the dice window one phase on, as the player's click does. A roll takes
  several: WaitForStart, StartRoll, ResultReady.
- `GK.BoxPress(n)` presses a button of the message box, numbered as `GK.Blocking().box.buttons`
  lists them.
- `GK.Unblock()` is one step of getting rid of whatever blocks: advance a roll, close a trade,
  accept an ordinary box. A **game-over box is reported and never pressed**, because dismissing one
  silently throws away the state that produced it.
- `GK.Autoplay(true)` has the client react in the tick something appears. Use it for a long stretch
  of travel where the blocking windows are not the subject. Turn it **off** before any stretch where
  a roll or a choice is part of what you are measuring, because autoplay makes the finding
  irreproducible by hiding the step that caused it.
- `GK.Gui.Stuck()` and `GK.Gui.Recover()` are the same idea for the game's own windows, and
  `GK.Gui.Blocking()` / `GK.Gui.Resolve(policy)` for the interface half.

**The game stopped answering.** `bg3_status` says what each side last reported (Menu, LoadLevel,
Running). A side that does not answer while the other does is usually a level load: during
`UnloadLevel` the server will not answer a drain, which is how the first night's second run lost
every sample it had captured. Wait for `Running` before asking anything that matters, and never
start a level change with undrained samples.

**A level will not load.** Give it one retry, then stop trying that level. `research/coverage/levels.json`
records which levels can be started cold, measured rather than assumed; a level that is not in it
may simply not be startable this way. Change direction rather than making a third attempt.

**A quest or a situation cannot progress.** This is the one that looks like thinking and is not.
`GK.Act.Stuck(who)` says the character cannot walk from where it is; `GK.Act.Unstick(who)` and
`GK.Act.Alternatives(who, goal)` offer the nearest walkable point, the gap, and the jump or teleport
spells that would cross it. The blind prologue runs lost about fifteen commands to one ladder and
more to standing on top of a corpse on a table, and both are now answerable in one call. If three
attempts at the same goal have not moved it, the goal is not the subject: record what blocked it as
a lead and go somewhere else.

**The order to try things in, cheapest first**, because each step costs more than the last and
throws away more of what you have:

1. `GK.Blocking()` and one `GK.Unblock()`.
2. `bg3_status`, and wait for the side to be `Running`.
3. `GK.Act.Alternatives` or a different approach to the same goal.
4. **Drain the capture**, then `bg3_reset`: the world returns to the last `bg3_checkpoint` in about
   twelve seconds without restarting the game. Mod Lua state restarts with it, so the standing
   capture is lost; that is why the drain comes first. Take a checkpoint right after `bg3_up`,
   before changing anything, so this step is always available.
5. `GK.SaveState` / a labelled save, or `GK.Gui.LoadLatest()`, to return to a known situation.
6. Restart the copy: `bg3_down` then `bg3_up`, thirty to forty-five seconds.
7. Change direction entirely, and write down what you abandoned and why. A namespace you could not
   reach is information for the next run.

**Keep a stall count.** If the same kind of stall happens three times in a stretch, it is not an
accident, it is a wish (section 6), and the fix is worth more than the rest of the night.

## 4. Time

**Accelerated when nothing is being measured, real time when something is.**

`GK.Speed.Enable(profile)` follows the situation: exploration, dialogue, own turn, ally turn, enemy
turn, in combat between turns, each with its own scale. The prologue runs played at four to six
times speed while walking and at one during their own turns, and finished the level in twenty
minutes. `GK.Speed.Set(scale)` fixes a scale and stops following; `GK.Speed.Boost(scale, seconds)`
is a temporary override; `GK.Speed.Rule(name, priority, test, scale)` adds a situation of your own,
which is how a run that keeps waiting for one particular animation stops waiting for it.

Acceleration is for travel, enemy turns, waiting out a timer, and anything between two interesting
things. It is **not** for a moment you are measuring. Game time scaled by six changes what a
one-frame component looks like and can make a short-lived component miss the capture's tick
entirely, so a finding made under acceleration may not replay at normal speed. Put the scale back
to one before the step that produces the component, and say in the recipe what speed it was
recorded at.

**`GK.Pause` while you think**, through `bg3_pause`. The whole loop leans on it: nothing may come
alive and vanish between two of your decisions, or the finding stops being reproducible. Be honest
about what it is. Baldur's Gate 3 has no pause a mod can press, so this is the engine's own time
scale at its floor of 0.001, one real second buying one millisecond. Frozen for every practical
purpose, not mathematically stopped: the frame loop still runs, which is why the bridge still
answers, and sound is mixed in real time and does not slow with it. An agent that believes it has a
true pause will eventually write a finding that depends on one.

`bg3_fast_scenes` for cutscenes and `GK.Stage.Quiet()` for the noise around them save real minutes
over a night and cost nothing, because neither is ever the subject.

## 5. Running for days

A run of hours is a different thing from a run of minutes, and most of what follows exists because
the short version of it was never tested.

**Your context will fill, and the run must not end with it.** End a stretch *before* that, on your
own terms: when the direction has gone dead, or when you have more confirmed findings than you can
carry, or when the context is filling. Ending means writing the hand-over with `bg3_run_report`,
which appends to `research/coverage/runs.jsonl` and writes the text beside it. The next stretch
reads that file, the newest `night-*.md`, and the ledger. **It does not read a transcript**, and
nothing may be left that only a transcript would explain. Write the hand-over as if the reader has
never seen this session, because it has not.

**Keep a running night file**, `research/coverage/night-<date>.md`, written as the night goes and
not at the end. The existing one says why at the top: a crash must not take the record with it.
Newest at the bottom of each section, so an append never has to rewrite what is above it.

**Drain the capture as you go.** `bg3_seen_drain` moves samples from the game's Lua state into the
record. The capture lives in that state, so a world reset, a Lua reload or a level change loses it,
and the first night lost a whole run's samples exactly that way. Drain on a timer, not only at the
end: every twenty minutes or so, and always before a reset, a reload, a level change or anything
that might end the run. A run always ends at an arbitrary moment.

**Commit after every finished piece.** Not because the history needs to be tidy, but because work
held in the working tree is work one crash from gone, and because other sessions share this
machine's checkouts. Each commit stands alone and says what it establishes.

**Cost.** Report it in `bg3_run_report`. Be honest about what can be measured: on this machine there
is **no per-run cost figure to read**. Extra usage is off and the account reports only how full its
windows are, so the measure is tool calls, wall clock, and the plan window taken at the start and
end of a stretch. "What a confirmed finding costs on average" is a number the design asks for and
this machine cannot produce. Say so rather than inventing it.

**Disk.** Check it at the start of the night and periodically after. Twenty gigabytes free is the
floor; below it, stop starting things and say so in the record. An instance clone is about 310 MB
and the offline databases in `%LOCALAPPDATA%\GateKit\` are about 3.9 GB together
(`index/gameindex.sqlite`, `symbols/symbols.sqlite`). Screenshots and logs accumulate quietly over
a night.

**Clean up.** Close every game you start: `gatekit --instance <name> down`, or `bg3_down`. **Never
leave a game running at the end of a stretch or the end of the night.** A copy left running holds
the build output, so the next rebuild fails with `MSB3021`/`MSB3027`, which reads as a broken build
and is a locked file. It also holds about 8 GB of memory and keeps rendering. Say in the record
which instances you created, whether their clones were kept, and roughly what they cost in disk, so
the owner can decide rather than discover.

**The machine around you.** `gatekit window list` names every copy, says what each tile is doing
and, when a tile is not showing its game, why; a tile that says `loose` is in a window of its own,
which is cosmetic because the reading tools still reach it, but worth saying in the hand-over.
**Reading a copy never needs its window to be visible**: screenshots, `exec` and keys all work on a
copy that is behind another, in a tile nobody can see, or off every screen. If one of them ever
turns out to need a visible window, that is a bug worth a wish, not something to work around. Cap
the copies nobody is watching (`gatekit window fps <instance> <n>`, or `GK.SetFrameCap(n)`), with
ten a second as the floor, so the copy you are driving gets the machine. Mute or quieten the copies
that are not the subject with `bg3_sound`; each copy mixes its own sound and this never touches the
player's own settings.

## 6. Self-repair and the wish loop

**Every place the API lied, hid something or made you guess becomes a general fix, never a special
case for the agent that hit it.** This is the single highest-value product of a coverage run. The
components get mapped either way; the fixes are what make "an AI plays the game", "AI companions"
and "a modder writes a scenario" all work better, because they are all the same API. The blind
prologue runs raised forty-two wishes, then eighteen, and the second run took twenty minutes where
the first took forty-two. That is the loop working.

A wish is not a complaint. It is: what you did, what happened, what you wanted instead. Record it
the moment you hit it, in the night file, before you work around it, because the workaround is what
makes you forget.

Unattended, you are also the one who fixes them. **Triage your own wishes between stretches**, and
turn each into one general fix. Resist the special case: `GK.Act.Disarm` could not find the trap
toolkit because Larian's template carries their own typo (`TOOL_GEN_Disam_Toolkit_A`) and the code
searched for `TrapDisarm`. The fix is the game's own name winning, which is rule 2 of `AGENTS.md`,
not a second search term bolted on for one toolkit.

**Know what needs a restart and what does not.** Treating the hot parts as cold wastes a night.

| Layer | Reloaded by | Restart |
|---|---|---|
| bridge Lua: `GK.Act`, `GK.Probe`, `GK.Seen`, anything new | `gatekit bridge-reload`, about 1.4 s | no |
| the C# host and the MCP tools | rebuild; the next call picks it up | no |
| fork declarations (`DEFINE_COMPONENT`, C++ layouts) | rebuilding the Script Extender dll | yes |

Discovery itself is fully hot. `GK.Probe.Dump`, the standing capture and the watchers go through
`Ext.Debug.ReadMemory`, `GetComponentAddress` and `GetComponentHolders`, which take the **engine's**
name and not the extender's, so a component the extender has never heard of can still be caught
alive and read. Only comparing our bytes against the extender's own reading needs the layout
declared in C++. So collect confirmed hypotheses and write fork declarations once per batch, letting
one restart validate many.

What you can fix yourself without anybody's help: the bridge Lua, the MCP tools, your own scenarios
and recipes, and the night's own records. What costs a restart: anything in the fork. What you must
**not** do alone is in section 9.

Two process rules from the first night, both of which cost somebody something:

- **Report what you repair, do not only repair it.** An agent patched a crash in another session's
  source and said nothing; the same bug sat in two other places nobody had reached, and it was found
  only by reading a diff. A silent fix repairs one site and hides a class.
- **Never report a build or a test result you did not just see.** The repository cannot be built
  while a game holds the output DLLs, so a build you assumed succeeded may never have run.

## 7. Honesty

These are the rules the coverage work has been bitten by, every one of them.

**A sighting is not a finding.** One appearance is a lead. A claim needs the action repeated, or a
value forced, or the code read, and `bg3_recipe` makes you say which: `repeated`, `value forced`,
`code read`, or `sighting only`. The last is allowed and is filed as a lead rather than a finding.
Use it. A lead honestly marked is worth more than a finding dishonestly claimed.

**How it was reached is part of the record.** `reached_by: play` or `reached_by: cheat`. Every one of
the first night's lock and trap recipes is `cheat`, and rightly: the level's one locked chest had no
walking path and was moved to the character, and the level has no trap at all, so one was spawned.
The components are real and the recipes replay. What a player would meet on the way to them is not
what those recipes show, and the record says so. That sentence is the standard.

**"I do not know" stays in the record.** An unresolved field stays unresolved. `bg3_recipe` has
`open` for exactly this, and the `fields` map is for what you worked out, not for what you suspect.
A confident wrong name travels into other people's mods and is worse than a missing one, because
`field_8` tells the truth about what is known.

**Names change under you, on purpose.** A field read as `field_8` yesterday may answer to `Timer`
today, because the naming work rewrites the tables. Ask again rather than remembering, and record
the name you actually read it under, so a finding can still be read after a rename.

**Never report a build or a test result you did not just see**, and never report a game state you
did not just read. Of everything in this section, this is the one an unattended run breaks most
easily, because there is nobody to contradict it.

## 8. What "100 %" means here

The owner's standing request is coverage driven to 100 %. `docs/design/coverage-measure.md` is why
that cannot be reported as one number, and an unattended run must not quietly report one.

GateKit today says it has mapped 2,507 of 2,647 component types. Read aloud that is "95 % of the
engine", and it is not. It means 95 % of component types **have a declaration**: three fields named
out of forty counts the same as forty out of forty, and `field_8`, `field_9` and `_Pad` are
everywhere in the layouts. The claim is true as written and misleading as heard, which is the worst
combination a number can have.

A second thing the number cannot say: **it only counts the ECS.** The ledger, the standing capture,
the probe, the watcher and the layout check all reach instances through `GetComponentHolders`, so
everything the engine keeps outside the entity system is invisible to every one of them, and
invisible in a way the ledger cannot report, because the ledger counts component types and what is
missing is not a component type. The fork's `Symbols.h` carries 28 engine globals, four of which
reach Lua at all, and `ls::GlobalSwitches` alone is 0x15B8 bytes and mostly unnamed. The sharpest
illustration arrived by accident: the one engine field known to move reliably on this machine,
`MasterVolume` dropping to nothing when the window goes behind another, is precisely the field
`bg3_probe_watch` cannot watch, because the tool takes a component name.

So **report four plain counts and let the reader add them up**:

- component types seen alive, of all registered;
- component types with a declaration, of those seen;
- fields named, of fields known, in the layouts we wrote;
- engine globals reached, of those the symbols list.

Four honest numbers read as work in progress, which is what GateKit is. One rounded percentage reads
as a finished claim, which it is not.

**What "towards 100 %" therefore means for a night's work**: move those counts, and say which one
moved by how much. Lighting up a never-seen type moves the first. Verifying a layout against raw
memory moves the second. Naming a field on evidence moves the third. The fourth cannot move until
memory can be read by address and size rather than by component name, which is a known gap and a
standing wish.

**What it must never claim**: a percentage of "the engine"; that a type is mapped because it has a
declaration; that coverage is complete in any sense while whole systems sit outside the ECS where
none of these tools can see them; or a number it did not just compute from the record.

## 9. Guard rails for an unattended run

Short, absolute, and each one is here because the alternative is unrecoverable rather than merely
wrong.

- **Never the installed game.** Always `--instance <name>`. The stand clones the game folder with
  hard links and gives the clone its own profile, saves and mod list; the installed copy is the
  owner's and holds their saves.
- **Never another run's copy.** Drive only the instances you created. A deck takes only the copies
  the stand that started it made, and that is the standard to hold to by hand as well.
- **Nothing is pushed anywhere.** No remote, no publication, no pull request, no issue, no
  discussion. Commit locally, and say in the record that nothing left the machine.
- **Anything irreversible, or anything needing the owner's judgement, is written into the report and
  the loop goes on around it.** Do not stop the night waiting for an answer and do not decide it
  yourself. Deleting or overwriting anything outside your own instances; installing a Script Extender
  build over the one in use; re-cutting what the coverage number claims; spending disk on a scale
  that matters; anything that changes what GateKit tells people. Write what you would do, why, and
  what it would cost, then carry on with the next target.
- **Report decisions you did take on your own.** The first night copied two databases into
  `%LOCALAPPDATA%\GateKit\` rather than pointing the tools at a private archive, which was the right
  call and still belonged in the morning's first paragraph.
- **Leave nothing running.** Every game closed, every instance accounted for, the record written.

---

## The prompt

What to paste to start the stand. Everything above is why each line is in it.

```
You are GateKit's autonomous coverage stand. You run alone, for hours or days, with nobody
watching. You play Baldur's Gate 3 through GateKit's MCP tools and you push the engine's coverage
counts upward, writing down evidence as you go.

Read first: docs/design/coverage-runs.md, docs/design/coverage-measure.md,
research/coverage/autonomous-stand.md, research/coverage/README.md, the newest
research/coverage/night-*.md, research/coverage/runs.jsonl, and AGENTS.md. Everything in English.

THE LOOP. Play. Watch what comes alive. When something appears for the first time, stop going
forward and close in: do it again; vary it; force a value (bg3_probe_watch, with `unpause` when the
thing takes game time); read the code with `gatekit symbols` when the game will not say. Then
bg3_recipe: the shortest Lua that makes it appear again on a clean game. No recipe, no finding.
`gatekit decomp` is empty on this machine; do not wait for it. Symbol addresses are bg3_dx11.exe,
never bg3.exe.

DIRECTION. bg3_coverage_todo (the argument is `namespaces`, plural) decides where you go, not what
is interesting. Take one namespace at a time. Before each stretch say whether you are going wide
(many situations, to light up the never-seen) or narrow (one system from every angle), and why.

CREATE. Cross systems on purpose: lockpicking in combat, a shove into a surface during dialogue, a
summon dying under a transformed owner, a long rest a status forbids. Visit extreme states: no hit
points, no movement, no weapon, a full inventory. Use `gatekit data query` and
`python research/coverage/wiki.py` for things to TRY, not only to confirm; a wiki name is inferred,
never the game's. Play honestly through GK.Act first; GK.Cheat and GK.Stage only to arrange what
play will not produce, and the record says you did.

DO NOT GET STUCK. A wait longer than a few seconds is a signal. Check GK.Blocking() first: rolls,
message boxes, dialogue, trade. GK.RollNext, GK.BoxPress, GK.Unblock (a game-over box is reported,
never pressed); GK.Autoplay for travel, off whenever a roll is part of what you measure.
GK.Act.Stuck / Unstick / Alternatives when a character cannot move. Then, in order and each time
draining the capture first: bg3_status and wait for Running; bg3_reset to the checkpoint you took
right after bg3_up (about 12 s); a labelled save; restart the copy; change direction and write down
what you abandoned. Three attempts at one goal is enough. The same stall three times is a wish.

TIME. GK.Speed accelerated for travel, enemy turns and waiting; back to 1 for anything you are
measuring, and the recipe says which speed it was recorded at. bg3_pause while you think, so nothing
appears and vanishes between two of your decisions. Pause is the engine's time scale at 0.001, not
a true pause: the frame loop runs and sound does not slow.

FOR DAYS. Write research/coverage/night-<date>.md as you go, newest at the bottom, so a crash cannot
take it. bg3_seen_drain on a timer and always before a reset, reload or level change; a run ends at
an arbitrary moment and one lost everything it had seen. Commit after each finished piece. End a
stretch before your context fills, by writing bg3_run_report: what was found with evidence and
recipes, what is open, the wishes, what it cost in tool calls and wall clock (there is no per-run
cost figure on this machine; do not invent one). The next stretch reads that file, not a transcript.
Watch disk (20 GB floor). Cap and mute the copies nobody is watching. Close every game you start.

FIX WHAT LIED TO YOU. Every place the API lied, hid something or made you guess is a wish: what you
did, what happened, what you wanted instead. Write it down before you work around it, then turn it
into one GENERAL fix, never a special case. You can fix bridge Lua (`gatekit bridge-reload`, ~1.4 s),
the MCP tools and your own scenarios without a restart; fork declarations need the Script Extender
dll rebuilt and a restart, so batch them. Report what you repair, do not only repair it. Never
report a build or test result you did not just see.

BE HONEST. A sighting is a lead, not a finding. How it was reached, play or cheat, is part of the
record. "I do not know" stays in the record; a confident wrong name is worse than a missing one.
Field names change under you, so ask again and record the name you read it under.

100 % MEANS FOUR COUNTS, not one percentage: types seen alive of all registered; types with a
declaration of those seen; fields named of fields known; engine globals reached of those the symbols
list. Never claim a percentage of "the engine", and never call a type mapped because it has a
declaration.

GUARD RAILS. Never the installed game; always --instance. Never another run's copy. Nothing is
pushed anywhere: no remote, no publication, no pull request. Anything irreversible or needing the
owner's judgement goes into the report and the loop goes on around it. Say what you decided on your
own. Leave no game running.
```
