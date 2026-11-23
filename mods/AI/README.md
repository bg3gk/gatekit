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
| `Mods/Gwynerva_GateKitAI/ScriptExtender/Lua/Server/` | `Bridge.lua` (the table `AI`, the rows, the channel to the client), `Mode.lua` (the mode and the session), `Say.lua` (the line over a head, the accent, what a target names), `Plan.lua` (the route plan, the notes), `Ways.lua` (the passages of `now.ways`, the reason a walk does not get there), `Turn.lua` (a turn's resources and actions, the countdown, the reasons), `Now.lua` (the `now` block), `Step.lua` (the four tools), `Stand.lua` (calls passed on by the client; a stand's scenes armed, armed again after a load, its checkpoint and reset) |
| `.../Lua/Client/` | `Bridge.lua`: what only the client knows (open windows, dialogue answers, a film) told to the server; the player's own clicks carried out in the UI's context. `Tool.lua`: the door every call comes in by |
| `.../Lua/Shared/` | `Encode.lua` (the answers' JSON, the session's file), `Scenes.lua` (scene states read generically), `Stand.lua` (a stand's manifest of scenes, the code kept), `EventKinds.json` (the mod's rows), `VerbClasses.json` (each verb's class) |
| `.../Lua/Plans/` | route plans by name |

`AI` is a global of the mod's own Lua environment, as `Weave` is of Weave's; from GateKit's
host and from other mods it is `Mods.GateKitAI.AI`. Its rows are a family of its own, `ai`,
added to GateKit's event log at start with `GK.EventLog.Register`. Annotations:
`lua-types/GateKitAI.lua`. Tests: `tests/ai/`.

## The four tools

| Tool | Does |
|---|---|
| `ai_session { mode, prompt, prompt_file, scenes_file, plan, step }` | begins a session and answers the owner's prompt and the whole picture; with no argument reads the session |
| `ai_step { do, target, spell, n, who, window, control, value, say, think, note }` | one act, the wait until the game has settled, what changed |
| `ai_look { part, detail }` | the whole picture or one part, when the agent is lost |
| `ai_scene { name, call, file }` | the owner's director commands: a named call of a staged scene; arming and disarming a stand's scene |

### One door, in every state of the game

The tools enter on the client (`"side": "client"` in `tools/ai.json`). While a level runs the
server says hello to the client every second, and the client passes each call to it over the
mod's channel and hands back its answer unchanged. Before a level is loaded - the splash screen,
the main menu, the new game's settings, a film, a load - the game has no server, and the client
answers by itself: `ai_step` with the acts a player has there (`ui`, `continue` on the splash
screen, `close` on a film or a box, `answer` on a box, `wait`), `ai_session` (mode, prompt,
scenes; a plan waits for a level), `ai_look` and `ai_scene` for scenes of the client and the UI.
Its `now` holds `where` (what holds the game, and the game's state), `waiting` (the splash screen,
a film, a window that waits over the others with its buttons, the open windows), `window` and, in
open mode, `scenes`.

A window that waits over the others - a message box (`Dialog_box`: the menus' and creation's
questions, the story's messages), a tutorial - is a line of `waiting` with its text, its numbered
buttons and the step that answers it, in a level as before one. While it stands, `do = ui` with a
control of another window is refused with that line: a player cannot press behind it. A call during which the level is left (the
creation's finish) answers from the client: its own answer is lost with the server.

Every answer is one line of JSON with the keys `ok`, `mode`, `did`, `why_not`, `hint`, `now`,
always all of them and in that order (`prompt` before `now` where a tool gives it). A refusal is
`ok = false` with nothing done, `why_not` saying why and `hint` giving the corrected call.

### `now`: only what changed

The bridge keeps a cursor: what the agent was last told. A key of `now` is present only when
its part changed, so a call after which nothing happened answers `"now":{}`.

| Key | Holds | Told again when |
|---|---|---|
| `where` | the level, the nearest named thing, the controlled character (`(controlled)`, or `nobody controlled` with the `select` that mends it) and its position | it moved two metres, changed level, or control changed |
| `fight` | `off`, or the round and whose turn is open (`(mine)`: a party member's), and the member the acts without `who` go to | any of it changed |
| `goals` | the agent's own goals, up to three (`goals` of `ai_step`, `Server/Goals.lua`): numbered, done ones ticked | they changed, or a party member's turn opened |
| `countdown` | each story timer: its own text, the rounds left, and the thing that text names with its distance and bearing when the mode lets the agent know it; `over` when the last one is gone | any of it changed |
| `can` | in a party member's open turn: what is left of it and every action available now ([A turn in a fight](#a-turn-in-a-fight)) | any of it changed |
| `party` | each member: hit points, what is in hand, conditions | any of it changed |
| `waiting` | what the game waits for from the player - a dialogue with its numbered answers, a roll window, a tutorial, loot, a prompt, a film - each line ending with the step that answers it; the open windows that are neither the HUD's nor one of those (`GK.Gui.Windows`), for `do = ui`; `[]` when it is gone | it changed |
| `window` | after `do = ui` with no control: the window's controls, one line each - handle, kind, text, value, what a repeated control stands for (`of`: its view model's text, an ability's row), a list's first items; after a press that opened a window or turned the pressed window's page (`GK.Gui.Shown` differs), that window's controls under a first line saying which | on that step |
| `ways` | the passages out of here (`GK.See.Passages`, `Server/Ways.lua`): ladders with the height each leads to, doors, portals with their target, then the mechanisms worked in place by the story's own use (buttons, organs, pods); `seen before` for one out of sight now | `+` new, `~` changed |
| `near` | creatures and usable things the party perceives, but for what `ways` lists, each with a handle (`#` and six characters of its uuid), how it stands to the party, distance and bearing from the controlled character (north = +z, east = +x), and the height when it is 1.5 m or more above or below | `+` new, `~` changed |
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
`waiting`, `party` and last `can` - what the agent acts on - are cut last
([A turn in a fight](#a-turn-in-a-fight)); `countdown` and `where` never. While `can` is told, the
cap grows by `AI.Detail.<detail>.can` (2500), the room of a turn's set. `ways` reaches farther than
`near`: `AI.Detail.<detail>.ways` metres (40) against `radius` (25). `ai_look` with
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
| `answer`, `continue`, `roll`, `take`, `close` | fair | `GK.Gui.Resolve`; `continue` on the splash screen raises the page's own event (`GK.Gui.Event`), `close` on a film presses Escape (`GK.Key`) |
| `ui` | fair | any window, by the universal verbs: `window` alone `GK.Gui.Look` (with `value`, its `find`); `control` `GK.Gui.Click`; `control` and `value` `GK.Gui.Set` (a number when the value reads as one) |
| `cast`, `attack` | fair | `GK.Act.Cast`, `GK.Act.Attack` on the client: the hotbar, the aim, the click |
| `examine` | fair | the game's Examine window, as its T key and the context menu's "Examine" open it: the HUD's `ExamineCommand` on the creature's view model (OverheadInfo's `Data.Characters`), the summary and details tabs read with `GK.Gui.Text`, the window closed. `did` holds what the window shows; its AC and resistances are kept per creature name (`AI.Examined`) and told in `can`'s enemies line |
| `end`, `select`, `wait` | fair | `GK.Act.EndTurn`, `GK.Act.Select` |
| `go`, `use`, `talk`, `loot`, `jump` | ordered | `GK.Cheat.Move`, `Use`, `Talk`, `Loot`, `Jump`: GateKit's scripted order. The character walks, reaches and uses as the game makes it, but the player's own click for these is not built in GateKit; when it is, these acts move to it |
| `cheat_cast`, `cheat_teleport` | cheat | `GK.Cheat.Cast` (a spell the hotbar does not hold), `GK.Teleport` |
| `plan` | - | the act the loaded plan names for its step (open mode) |

Each step's row says which way it went (`way`). `AI.Acts` is data: an act is a row of it.
A `go`, `use`, `talk` or `loot` that the pathfinder refuses answers why (`AI.Ways.WhyNot`): the
target's height against the actor's (another floor) or the way round, the nearest point a walk
reaches and whether a jump crosses the rest (`GK.Act.Alternatives`), and the nearest passage the
agent knows of other than the target itself - the nearest a walk from here reaches, when one
does - whose `use` is the hint (`go` for an open door: its `use` would close it). An actor that
stands where no walk starts (GateKit's `Act.Stuck`: on a cradle, a table) is told so first, with
a jump down to the nearest floor as the hint. In a fight a `go` that stopped short says the
movement left this turn, or that it is spent. A `go` that ends more than
2.5 m short of its goal says where it stopped and how far short, never "walked to" the goal. In a
fight a `go` that leaves an enemy's reach is refused with who would attack; `provoke = true` walks
anyway. A `use` of a ladder walks to the end nearer the actor's height (GateKit's
`Act.ApproachPoint`).

With the custom difficulty's "Hide NPC health" (the ruleset modifier `HIDE_NPC_HP`, which the
health bars' XAML follows; `AI.HideNpcHp`) neither `near` nor `can` names a creature's hit points.

A creature the game marks a boss (`Osi.IsBoss`; its health bar over the screen then has the boss
frame, `TargetHealthBarTemplate`'s trigger on `CharacterType` `Boss`) is `boss` in its line of
`near` and in `can`'s enemies line, beside its race (`AI.RaceOf`) when the name does not hold it.

A `note` with `about` (a handle or a name, resolved as a target is) is of that creature or thing
rather than of the place (`AI.Notes.Add(text, about)`, the same file of the copy: it outlives a
load). A line of `near` or of `can`'s enemies that names it ends with `[N notes: "the last, cut"]`
(`AI.Notes.Mark`); `ai_look { part = "notes", about }` reads every one. The notes of the place
(`now.notes`) leave those out.

`goals` on any step sets the agent's plan: up to `AI.GoalsShow.max` (3) goals separated by `|`, a
goal starting with `✓` done, `""` none. The bridge keeps them, tells them back in `now.goals` and
shows them over the acting member's head one after another (`GK.Say.Line`; open goals in violet,
done ones light grey with a tick; `AI.GoalsShow.rounds` (2) passes, `gap` (2) seconds of the game's
time apart) when they are set and when a party member's turn opens. A row `ai_goals` keeps them.
Nothing in them is the bridge's own.

An `answer` in a dialogue first shows the chosen answer in the theme's violet (`GK.Cheat.Mark`) for
`AI.MarkSeconds` (1) of the game's time, then presses it: whoever watches sees what the agent
chose. 0 presses at once.

One step runs at a time (`AI.StepRunning`): a call that comes while another runs is refused, and a
`wait` then answers when that step has ended and the game has settled. On the client, a call
whose server is silent is taken as lost with the level only when the game's own state has left
`Running`.

What the bridge has named to the agent by a handle in blind mode (`AI.Told`, written by
`AI.Util.handle`) stays known to it: a target from `ways`, `near` or the countdown resolves. A
session that sets a mode forgets them.

Outside a fight and a dialogue, `AI.Stuck.steps` (10) walking steps (`go`, `use`, `talk`, `loot`,
`jump`) that end within `AI.Stuck.radius` (6)
metres of where the count began add a line to `waiting`, every `AI.Stuck.every` (5) steps: the
count, the place and up to four ways the agent knows there, nearest first, those it has used marked
`(used)` (`AI.Tried`, every `use` target): going back is a way as much as going on - and the
open ground the party sees by bearing (`AI.Ways.Open` over GateKit's `GK.See.Ways`: a corridor or
a deck is a way out with no thing to name). It is a fact, not an act to answer. `ai_look` with
`part = "ways"` ends with the same open ground; a look at one part reads it with the sizes of
`full`.

In blind mode a door, a ladder or a portal is seen within `AI.Ways.View` (40) metres when a clear
line runs to it from a member's eyes (`GK.See.Around`'s `view`): the game's own sight range, about
18 m, is its creatures' perception, shorter than what a player sees on the screen. Creatures stay
on the game's sight. `ways` lists what is in sight now before what was seen before, the nearest
first.
`say` shows a line over the character's head with a glow on the target (`AI.Say`) once the act
has begun - the verb took it on, or the client saw it done - and never for an act that is
refused; the row's `shown` says which. `think` is the step's reason: kept in the row only, never
shown in the game; with `AI.Think.required` (data, off by default) a step without it is refused.
`ai_look { part = "thoughts" }` reads the last reasons (`AI.Thoughts`, six or with `detail = "full"`
thirty) and moves no cursor and writes no row, so a listener reads them without disturbing the
player: `gatekit --instance <copy> mcp call ai_look '{"part":"thoughts"}'`. `note` keeps a note
with the place.

An act that needs the player's selection (`cast`, `attack`, `end`) selects the member first when
the game's own flag (`Osi.IsControlled`) says it is not controlled, as after a death or a fight's
end, when the game may control nobody.

### A turn in a fight

`now.can` is the turn of the member whose turn is open as the game counts it (`AI.Can`,
`Server/Turn.lua`), told again whenever any of it changed. Nothing in it is ranked or advised:
how the agent weighs it is the owner's prompt and the model's.

    Lae'zel: action 1, bonus 1, reaction 1, movement 9 m; slots 1:2/2
    leaving the reach of Imp #a1b2c3 provokes an opportunity attack
    enemies: Imp #a1b2c3 1.4 m 8 hp, #d4e5f6 7.2 m 13 hp
    action: Projectile_MainHandAttack 18 m: 85%, all in reach | Target_MainHandAttack 1.5 m: 95%, in reach #a1b2c3 | Shout_Dash, Shout_Disengage
    bonus: Target_PommelStrike 1.5 m: 95%, in reach #a1b2c3 | Shout_SecondWind | Target_Shove 1.5 m
    no target now: Target_SneakAttack
    too little movement: Projectile_Jump (3 m)
    also open: Astarion (action 1, bonus 1, reaction 1, movement 9 m)

- **Left**: the member's `ActionResources` (`GK.See.Resources`): action, bonus action, reaction,
  movement in metres, spell slots by level, and every other resource with a maximum above zero,
  by the game's name.
- **Threat**: the game's own list of those threatening the member (`CombatIsThreatened`), hostile,
  alive and with a reaction left; nothing once it has disengaged.
- **Enemies**: the hostiles in the fight the party perceives within 40 m, the nearest `list` of
  them with distance and hit points (a name once for a run of it, a distance and hit points once
  for a run of equal ones), and how many are farther.
- **Actions**: every ready action of the member's spell book (`GK.See.Actions`), grouped by what
  it spends - `action`, `bonus`, `free` - in the book's order, each as `cast` names it, with its
  range and what it costs beyond its group (a slot, movement, a class resource). For an action
  aimed at a creature: the tooltip's chance (`GK.See.Odds`) once, and its reach - `all in reach`,
  `in reach #a1b2c3, ...` (the enemies it reaches without a walk), or `nearest #a1b2c3 +4.2 m`
  (the walk to the nearest it reaches with the movement left); the other enemies' distances are
  the enemies line's. Where the chances differ, each enemy with its own. An enemy the game's
  target conditions refuse (`GK.Conditions`) is left out. Actions with the same range, cost and
  reach share one entry; variants of one action are written once
  (`Projectile_ChromaticOrb_{Acid,Fire}` for `Projectile_ChromaticOrb_Acid` and `..._Fire`). Entries
  that name targets come first, then those that cost nothing more (Dash, Disengage), then the
  rest; a line longer than `AI.Detail.<detail>.line` ends with how many were left out and the
  look that lists them all. An action the game gives no chance for (a contest such as Shove, a
  jump at a point) is listed with its range only.
- **Not now**: `no target now` - aimed at a creature, reaching none now (out of range, or the
  game's conditions refuse, as a sneak attack without advantage); `too little movement` - its
  cost in movement is more than is left.
- **Also open**: the other party members whose turn is open, with what each has left.

A step the game refuses in the member's open turn, outside a dialogue, says what is left in
`why_not` and the action lines that still fit in `hint`. An act of a downed member (the game's
status `DOWNED`: its turn is the death saving throw) is refused before it goes anywhere, with the
same act for another member whose turn is open as the hint. A `jump`, or a `cast` of a jump, Misty
Step or a teleport at a point, after which the caster stands within a metre of where it began says
so. A spell that needs preparing and is not prepared is not in `can` (`GK.See.Actions`: not ready).
Thirty variants of one action ("Shout_DisguiseSelf_...") are one entry with their count
(`AI.VariantsFold`, 4 or more) except in `detail = "full"`; the opportunity-attack line writes a
name once for a run of it. `party` says `downed` (the status) or `stands at 0 (immortal)`.

The block's cap holds `can` too: when the block is still over the cap after the other parts gave
way, `can`'s lines are cut to twice the text's length, then to once, with their count in
`more.can`; `ai_look { part = "can" }` reads them whole.

## Two modes

The mode is set by `ai_session` and named in every answer (`mode`). It is decided in one file,
`Server/Mode.lua`, which every part of the bridge asks; GateKit already classes what it reads
the same way, by a reader's scope (`seen`, `known`, `all`) and a row's `knows`.

| | `blind` | `open` |
|---|---|---|
| creatures and things in `near`, `ways` | what a member's sight holds now (the game's sight state, `GK.See.InSight`: not behind a wall or a closed door, on another floor, in magical darkness or beyond the sight range; in every direction); `ways` also what was once in sight | what the party is shown; a look also lists what is within the radius and not perceived, marked `unseen` |
| a target of an act | a party member, what a member's sight holds now, what was once in sight on the level; anything else is refused: "not knowable to a player: ..." | anything on the level |
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
A call answers what the function returned: a text, a number or a flag is said, a table
`{ ok = false, error, hint }` refuses with them (the scene said not now), another table says its
`answer` text; a call of a client's scene that answers an observation (`GK.Observe`) is followed
until it ends. A scene of the client's is read once in thirty frames and listed; its call is run
on the client, in the UI's context, where the window verbs work. A scene armed by this mod runs in
GateKit's environment, one armed by `gatekit exec` in the global one, which GateKit's environment
falls back to: the mod keeps one table of scenes for both (`AI.SceneTable`), moving a `GKScene`
made in GateKit's environment into the global one.

**A stand's scenes.** The session points at a stand's manifest (`ai_session { scenes_file }`), a
JSON object naming each scene, its side and its files; the mod names none of them
(`Shared/Stand.lua` has the format). `ai_scene { name, call: "arm", file }` arms one: the host
reads the file (`"files"` of `tools/ai.json`), the mod runs it on the scene's side in GateKit's
environment - where `gatekit exec` runs it - with the manifest's `options` set in `GKSceneOptions`
first, and keeps the code in the copy. From then on `ai_scene { name, call: "arm" }` arms it with
no file, `"off"` disarms it, and `ai_scene {}` lists the manifest's scenes not armed with the call
that arms each. A load restarts Lua: the server then arms again every scene the session armed on
that level, from the kept code (row `ai_scene`, `call = rearm`); a reset of Lua alone in a running
level (`gatekit reload`) does not, since the world kept their figures.

**Checkpoint and reset.** A manifest's `checkpoint` names a flag of its scene: when the flag turns
true while the game runs, the mod makes the game's autosave (`Osi.AutoSave`, autosaves allowed
for it and closed again). `reset` names another: when it turns true the client waits for the
game-over screen, holds it `reset_hold` seconds (4), and loads the newest save through the
screen's own Load (`GK.Cheat.Recover`); the load arms the scenes again. Each is one row
`ai_scene`. The host's own `checkpoint` and `reset` are not reached from Lua.

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
- A step that ends in a level change (the prologue's last, the creation's finish) loses the
  server's answer: the client answers from what it sees.
- A file given to the host for one argument (`prompt_file`, `scenes_file`, `file`) holds at most
  16,000 characters; a larger scene stays a direct `gatekit exec`.
- Calls answered by the client before a level is loaded write no row: the family `ai` is the
  server's.
- `say` before a level is loaded shows nothing: there is no character.
- Taking control of another party member (`select`, or `who` on `cast` and `attack`) is followed
  by ten ticks of waiting before the click: the client holds the newly controlled character whole
  only then.
- `near` is read from the controlled character: a change of control tells again what that
  member perceives.
- The server offers the owner's prompt as a tool answer only, not as an MCP prompt or resource.
- A countdown's goal is the thing whose name stands in the timer's text, compared case-blind for
  ASCII and Cyrillic only; the timer itself links to no entity.
- A Lua reset in a fight (`gatekit reload`) forgets which members of a party turn group have ended
  their turn: `can` and the default actor offer that member again.

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
