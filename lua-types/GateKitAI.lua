---@meta
--- GateKit AI (mods/AI): the bridge for an AI agent that plays. `AI` is a global of the mod's own
--- Lua environment; from GateKit's host and other mods it is `Mods.GateKitAI.AI`. Server side
--- unless a line says otherwise. mods/AI/README.md.

---@class AI.Answer
---@field ok boolean
---@field mode "open"|"blind"
---@field did string @ what was done; "" when nothing was
---@field why_not string @ why nothing was done; "" when it was
---@field hint string @ the corrected call, for a refusal
---@field prompt string? @ the owner's prompt, where a tool gives it
---@field now AI.NowBlock

---@class AI.NowBlock
---@field where string?
---@field fight string?
---@field can string[]?
---@field party string[]?
---@field waiting string[]?
---@field near string[]?
---@field heard string[]?
---@field events string[]?
---@field rolls string[]? @ open mode
---@field scenes string[]? @ open mode
---@field route string? @ open mode
---@field notes string[]?
---@field prompt string? @ a whole look's one-line reminder that a prompt stands
---@field more table<string, integer>?

---@class AI
---@field GK GK @ GateKit's API
---@field Class table<string, "fair"|"cheat"|"observation"> @ each verb's class (Shared/VerbClasses.json)
---@field Acts table<string, { way: "fair"|"ordered"|"cheat", needs: string[]?, about: string, run: function }> @ the acts of a step
---@field Cap integer @ ticks a step waits at most
---@field Quiet integer @ ticks with nothing going on after which the game counts as settled
---@field Detail table<string, { chars: integer, list: integer, text: integer, radius: number }> @ the sizes of `now`: brief, full
---@field Limit { chars: integer, list: integer, text: integer, radius: number } @ the sizes a step uses
---@field NowKeys string[] @ the keys of `now`, in order
---@field Styles table<string, table> @ the looks of a line and its accent, by name
---@field MaxChars integer @ the longest line shown over a head
---@field Sessions table<string, table> @ the cursors, by session name
---@field Client { open: table[], blocking: table?, scenes: table<string, string> } @ what the client last told
AI = {}

---The session's start (mode, prompt, prompt_from, plan, step) or, with none of them, its reading. Observed never. Cheat.
---@param args { mode: ("open"|"blind")?, prompt: string?, prompt_from: string?, plan: string?, step: integer? }?
---@return AI.Answer
function AI.SessionCall(args) end

---One act, the wait until the game has settled, the answer. Observed. Cheat.
---@param args { ["do"]: string, target: string?, spell: string?, n: integer?, who: string?, say: string?, think: string?, note: string? }
---@return AI.Answer
function AI.Step(args) end

---The whole picture, or one part. Moves the session's cursor; one row `ai_look`.
---@param args { part: string?, detail: ("brief"|"full")? }?
---@return AI.Answer
function AI.Look(args) end

---A scene under GKScene: every scene, one scene's state and calls, or one call run. Cheat.
---@param args { name: string?, call: string? }?
---@return AI.Answer
function AI.Scene(args) end

---One of the four tools ("session", "step", "look", "scene") as the text a tool answers: one line of JSON, keys in a fixed order. Observed when the tool is.
---@param name string
---@param args table?
---@return string
function AI.Tool(name, args) end

---The `now` block and its length as JSON text. Observation; moves the cursor unless `peek`.
---@param options { session: string?, full: boolean?, detail: ("brief"|"full")?, peek: boolean?, part: string?, wide: boolean? }?
---@return AI.NowBlock, integer
function AI.Now(options) end

---JSON text on one line, an object's keys in `order` first and the rest sorted, text as UTF-8.
---@param value any
---@param order string[]?
---@return string
function AI.Encode(value, order) end

---What waits for the player: the lines of `waiting`, whether one can be answered now, why the game goes on by itself.
---@param limits table?
---@return { list: string[], kinds: table<string, boolean>, actionable: boolean?, busy: string?, choice: integer?, spoken: string? }
function AI.Waiting(limits) end

---Whose turn is open: { on, round, turn = { { id, name, mine } }, mine }.
---@return { on: boolean, round: integer?, turn: { id: string, name: string, mine: boolean }[]?, mine: string? }
function AI.Turn() end

---A line over the character's head and, with `at`, the accent on it. Observed once the camera came back. Cheat.
---@param args { say: string?, at: (string|number[])?, who: string?, think: string?, ["do"]: string?, style: (string|table)? }|string
---@return table
function AI.Say(args) end

---The accent alone: a glow, a sound, the camera there and back, a marker when the style asks. Observed. Cheat.
---@param at string|number[]
---@param style (string|table)?
---@return table
function AI.Accent(at, style) end

---What a target names: a point ({x, y, z} or "x,y,z"), a uuid, a handle ("#" and six or more characters of a uuid), a name. In blind mode only what the party sees or has seen. The point or the uuid, or nil and why.
---@param at string|number[]
---@return (string|number[])?, string?
function AI.Resolve(at) end

---A row of the mod's family `ai` in GateKit's event log; nil when it could not be written.
---@param kind string
---@param payload table?
---@return table?
function AI.Row(kind, payload) end

---An act carried out on the client, in the UI's context; `done(answer)` is called with its answer.
---@param act { act: "resolve"|"cast"|"attack", policy: table?, only: table?, never: table?, again: string?, spell: string?, target: (string|number[])? }
---@param done fun(answer: table)
---@return integer
function AI.Ask(act, done) end

---One scene's state in a line. Both sides.
---@param scene table
---@return string
function AI.SceneLine(scene) end

---The names of the functions a scene offers, sorted. Both sides.
---@param scene table
---@return string[]
function AI.SceneCalls(scene) end

---Every scene of this side: name -> its line. `root` is GateKit's environment (Mods.GateKit). Both sides.
---@param root table
---@return table<string, string>
function AI.SceneLines(root) end

---A file of the mod's Lua folder read as JSON, or nil.
---@param path string
---@return table?
function AI.ReadJson(path) end

---@class AI.Mode
---@field name "open"|"blind"
---@field Names table<string, string>
AI.Mode = {}

---@return boolean
function AI.Mode.Blind() end
---The sentence a refusal of the blind mode begins with.
---@param what string
---@return string
function AI.Mode.Refuse(what) end
---The scope GK.See's readers are asked with: blind "seen" (with `memory`: "known"), open "all".
---@param memory boolean?
---@return "seen"|"known"|"all"
function AI.Mode.Scope(memory) end
---Whether a row of the event log may be told.
---@param row table
---@return boolean
function AI.Mode.Row(row) end
---Whether a part of `now` or of a look is told in this mode.
---@param key string
---@return boolean
function AI.Mode.Part(key) end
---Whether a thing may be named to the agent or acted on by it.
---@param id string
---@return boolean
function AI.Mode.Knows(id) end

---@class AI.Session
---@field prompt string? @ the owner's prompt
---@field promptFrom string? @ the file the host read it from
---@field MaxPrompt integer
AI.Session = {}

---Begins a session: mode, prompt, plan. Kept in a file of the copy. Cheat.
---@param args { mode: ("open"|"blind")?, prompt: string?, prompt_from: string?, plan: string?, step: integer? }?
---@return { ok: boolean, error: string?, hint: string? }
function AI.Session.Start(args) end

---@class AI.Plan
---@field Current table? @ the plan loaded
---@field Index integer? @ the step it stands on
AI.Plan = {}

---@param plan string|table @ a plan of the mod by name, or the plan itself
---@param index integer?
---@return { ok: boolean, name: string?, steps: integer?, error: string? }
function AI.Plan.Load(plan, index) end
---@return { ok: boolean }
function AI.Plan.Off() end
---@return table?
function AI.Plan.Step() end
---The next step in one line, after passing every step whose condition of arrival is true.
---@return string?
function AI.Plan.Line() end
---@param to "next"|"back"|integer
---@return { ok: boolean, step: integer?, of: integer?, error: string? }
function AI.Plan.Go(to) end
---Told what a step has just done: a plan step with no condition whose act this is, is passed.
---@param args table
---@return boolean
function AI.Plan.Did(args) end

---@class AI.Notes
---@field Reach number @ metres within which a note is told again
---@field Max integer
AI.Notes = {}

---@param text string
---@return { ok: boolean, n: integer?, error: string? }
function AI.Notes.Add(text) end
---@param all boolean? @ every note of the level
---@return { n: integer, text: string }[]
function AI.Notes.Near(all) end
---@return { ok: boolean, forgot: integer }
function AI.Notes.Clear() end
