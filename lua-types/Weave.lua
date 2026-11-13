---@meta
-- LuaLS annotations for the public API of Weave, the UI framework of GateKit (client side only).
-- Derived from Client/Kit/*.lua of the old repo's bridge mod (kit version 4, 2026-11-13).
-- The table is the global `Weave` of the Weave mod's Lua environment; other mods reach it as
-- Mods.GateKitWeave.Weave, GateKit also as GK.Weave (old spelling). See mods/Weave/README.md and
-- docs/contracts/lua-api.md. Every function below is CLIENT side. User copies of files
-- (Weave.Eject) live in Script Extender/Gwynerva_Weave/ui/.
--
-- Weave needs GateKit's Script Extender build on the v34 base (drawing behind elements, geometry
-- read back, styles that can be taken back, per-element text size). Where it is missing,
-- Weave.Mount returns nil and the kit says why once in the console.
--
-- The language (templates, expressions, styles, utility classes) is described in docs/weave.md;
-- this file is the Lua surface only. Internal helpers (Weave.Internal, the Backend functions the
-- kit itself calls, parser internals, OnFrame) are NOT part of the contract.

-- =========================================================================================
-- Types

---A Weave element (what a template tag became). Obtained from Document:Get / Query.
---@class WeaveElement
---@field tag string
---@field attrs table<string, string>
---@field label string?
---@field classes table<string, boolean>
---@field parent WeaveElement?
---@field doc WeaveDocument
local Element = {}

---Event passed to listeners.
---@class WeaveEvent
---@field type string
---@field target WeaveElement
---@field value any
---@field doc WeaveDocument
---@field user boolean @ caused by the user (false when raised by Element:Fire from code)

---Listens to an event on the element: click, dblclick, rightclick, change, hover, leave, focus,
---blur, expand, collapse; on a window: close.
---@param event string
---@param fn fun(event: WeaveEvent)
---@return WeaveElement self
function Element:On(event, fn) end

---Raises an event as if the user had caused it (a test clicks a button this way).
---@param event string
---@param fromUser boolean?
function Element:Fire(event, fromUser) end

---The element's value: a checkbox's state, an input's text, a combo's chosen option, a slider's
---number. For the rest: the label.
---@return any
function Element:Value() end

---Sets the value; raises no event.
---@param value any
function Element:SetValue(value) end

---Changes what the element says: a text's text, a button's caption, a window's title.
---@param text string
function Element:SetText(text) end

---Replaces a combo's options: { "a", "b" } or { { value = , label = } }.
---@param list (string|{ value: any, label: string })[]
function Element:SetOptions(list) end

---@param visible boolean?
function Element:Show(visible) end

function Element:Hide() end

---@param name string
---@return boolean
function Element:HasClass(name) end

---Classes drive the style sheet, as in a browser.
---@param name string
function Element:AddClass(name) end

---@param name string
function Element:RemoveClass(name) end

---@param name string
---@param on boolean?
function Element:ToggleClass(name, on) end

---Sets inline style: el:Style("color", "#ff8080") or el:Style("color: red; font: +1").
---@param name string
---@param value string?
function Element:Style(name, value) end

---Puts the keyboard cursor into the element (an input) or brings a window to the front.
function Element:Focus() end

---Opens a <popup>.
function Element:Popup() end

---Selects a <tab>.
function Element:Select() end

---Opens a <header> or a <tree>; Expand(false) closes it.
---@param on boolean?
function Element:Expand(on) end

---The first element inside this one that matches a selector.
---@param selector string
---@return WeaveElement|nil
function Element:Find(selector) end

---Greys the element out and stops it answering (class "disabled" is added for the look).
---@param on boolean?
function Element:Disable(on) end

function Element:Enable() end

---Replaces what a <canvas> shows: a list of drawing commands (see docs/weave.md, "Canvas").
---@param commands table[]
function Element:Draw(commands) end

---Builds more elements inside this one from template text. Returns the new elements.
---@param xml string
---@return WeaveElement[]
function Element:Append(xml) end

---Removes the element and everything inside it.
function Element:Remove() end

---Removes everything inside the element.
function Element:Clear() end

---A mounted component with its style sheets: a live window.
---@class WeaveDocument
---@field name string
---@field file string
---@field props table
---@field env table?
---@field state table @ the root component's reactive state
---@field root WeaveElement
---@field overlay boolean @ takes part in the overlay (input blocking, Esc, rescue); template attr overlay="false" turns it off
local Document = {}

---Sets a value of the root component's state (or several: doc:Set({ a = 1, b = 2 })) and brings
---the window up to date at once.
---@param key string|table<string, any>
---@param value any?
function Document:Set(key, value) end

---A value as the template sees it: state, computed or a property of the root component.
---@param key string
---@return any
function Document:Value(key) end

---Calls a function the root component's <script> defined.
---@param name string
---@param ... any
---@return any
function Document:Call(name, ...) end

---The element with this id ("#name" or "name"), or the first that matches a selector.
---@param selector string
---@return WeaveElement|nil
function Document:Get(selector) end

---Every element matching a selector list, in document order.
---@param selector string
---@param within WeaveElement?
---@return WeaveElement[]
function Document:Query(selector, within) end

---Listens on every element a selector matches now.
---@param selector string
---@param event string
---@param fn fun(event: WeaveEvent)
function Document:On(selector, event, fn) end

---Opens (default) or closes the window.
---@param on boolean?
function Document:Open(on) end

function Document:Close() end

function Document:Toggle() end

---@return boolean
function Document:IsOpen() end

---Reads the file again and builds the window anew (what saving the template does).
function Document:Reload() end

---Destroys the window and forgets the document.
function Document:Destroy() end

---Options of Weave.Mount.
---@class WeaveMountOptions
---@field props table? @ properties given to the root component
---@field env table? @ extra names expressions and scripts can see (functions the mod provides)
---@field file string? @ another file than <name>.weave

---Style variable preset for a theme named in `@theme name { ... }`.
---@alias WeaveThemeName string

---Definition of a custom style property (Weave.RegisterProperty).
---@class WeavePropertyDef
---@field parse fun(value: string, tag: string): any|nil, string? @ parsed value, or nil and why not
---@field apply fun(el: WeaveElement, parsed: any)
---@field remove (fun(el: WeaveElement))? @ without it the window is built anew when the property stops applying

---Definition of a custom pseudo-class.
---@class WeavePseudoDef
---@field test fun(el: WeaveElement, ...): boolean
---@field structural boolean? @ depends on siblings/children: restyle when a list changes

---Definition of a utility variant (Weave.RegisterVariant).
---@class WeaveVariantDef
---@field media table<string, number|string>? @ e.g. { ["min-width"] = 640 }
---@field container table<string, number|string>?
---@field pseudo string? @ a state: a colour slot ("hover", "active", "selected")
---@field selector string? @ what the element has to match, written as in a style sheet

-- =========================================================================================
-- The global table

---The Weave kit. Defined by mods/Weave (ScriptExtender/Lua/Client/*.lua); `Weave.Backend` is the renderer seam.
---@class Weave
---@field Version integer @ the kit's version (4). Grows when something is added; mods check `(Weave.Version or 0) < 4`
---@field Fonts string[] @ { "Tiny", "Small", "Medium", "Default", "Large", "Big" }
---@field FontSizes table<string, integer> @ Tiny 24, Small 28, Medium 32, Default 36, Large 40, Big 44 (font units)
---@field Globals table<string, any> @ functions every expression can call; add your own: Weave.Globals.clamp = fn
---@field Bindings table<string, fun(el: WeaveElement, value: any)> @ what a `:name="expr"` binding does; add: Weave.Bindings.tip = fn
---@field Kinds table @ element kinds table of the style engine (advanced)
---@field LayoutModes table<string, true> @ registered `display:` modes
---@field Stretchable table<string, true> @ tags whose width the layout may set
---@field StretchableY table<string, true> @ tags whose height the layout may set
---@field FontFiles table<string, string> @ font-family name -> file (see RegisterFont)
---@field Backend WeaveBackend @ the renderer; only for people writing a second renderer
Weave = {}

-- ----- Mounting and documents -------------------------------------------------------------

---Loads the component `<name>.weave` (the user's ui folder first, then registered components and
---roots) and builds its window, closed. Mounting a name again replaces the earlier window.
---Returns nil when this Script Extender cannot run Weave (said once in the console).
---@param name string
---@param opts WeaveMountOptions?
---@return WeaveDocument|nil
function Weave.Mount(name, opts) end

---A loaded document by name.
---@param name string
---@return WeaveDocument|nil
function Weave.Document(name) end

---The names of the loaded documents, in mount order.
---@return string[]
function Weave.Documents() end

---Is any kit window that takes part in the overlay open? (The HUD button does not.)
---@return boolean
function Weave.AnyOpen() end

---Closes the kit window opened last. Returns whether one was open.
---@return boolean
function Weave.CloseTop() end

---Brings an IMGUI window back into the picture if it is out of reach. Returns true when it had
---to be moved or made smaller.
---@param window any @ an IMGUI window object
---@return boolean
function Weave.KeepOnScreen(window) end

---Puts every kit window back to the place and size its template gives it.
function Weave.ResetWindows() end

---A document as plain data: every element with tag, id, classes, label, value and the style
---applied to it. For tests and assistants (no screenshot).
---@param name string
---@param selector string?
---@return table
function Weave.Dump(name, selector) end

---Raises an event on an element of a loaded document, as the user would; sets `value` first when given.
---@param name string
---@param selector string
---@param event string
---@param value any?
---@return any
function Weave.Fire(name, selector, event, value) end

-- ----- Component sources -------------------------------------------------------------------

---Gives a component as text: Weave.RegisterComponent("stat-card", "<template>...").
---@param name string @ tag name; stored as "<name>.weave"
---@param text string
function Weave.RegisterComponent(name, text) end

---Gives a template or a sheet as text instead of a file: Weave.Define("hello.xml", "<window>...").
---@param file string
---@param text string
function Weave.Define(file, text) end

---The text of a kit file and where it came from, or nil.
---@param file string
---@return string|nil text
---@return string? origin
function Weave.Source(file) end

---Adds a folder to look for templates and sheets in; later roots are searched first.
---@param prefix string @ ends with "/", e.g. "Mods/MyMod/ScriptExtender/Lua/ui/"
---@param context "data"|"user"? @ "data": inside the game's Data (a mod's pak); "user": the Script Extender folder
function Weave.AddRoot(prefix, context) end

---Copies a built-in file into the user's ui folder, where it can be edited while the game runs.
---Returns the path written.
---@param file string
---@return string
function Weave.Eject(file) end

---What can differ between set-ups the kit runs on.
---@return { files: boolean } @ files: pictures from files of a mod are drawn (DX11 yes, Vulkan not yet)
function Weave.Has() end

-- ----- Themes and hand-built windows ---------------------------------------------------------

---The themes the loaded sheets define, in order.
---@return WeaveThemeName[]
function Weave.Themes() end

---Switches the theme (one of Weave.Themes()). Returns the theme now in use.
---@param name WeaveThemeName
---@return WeaveThemeName
function Weave.SetTheme(name) end

---@return WeaveThemeName
function Weave.Theme() end

---The overlay's text size: "" (Script Extender's own) or one of Weave.Fonts.
---@param name string
function Weave.SetFont(name) end

---@return string
function Weave.Font() end

---A theme variable's value as written ("--accent" -> "#c8a45c"), variables inside it resolved.
---@param name string
---@return string|nil
function Weave.Var(name) end

---A colour for widget:SetColor from a variable name ("--log-error") or a colour as written.
---@param value string
---@param fallback number[]?
---@return number[] @ { r, g, b, a }
function Weave.Color(value, fallback) end

---Styles a widget built by hand with the base sheets, as if it were the element <tag id= class=>,
---and again whenever the theme or those sheets change.
---@param widget any @ an IMGUI widget
---@param tag string
---@param id string?
---@param classes string[]?
function Weave.Skin(widget, tag, id, classes) end

---Calls fn() after the theme, the text size or the base sheets changed.
---@param fn fun()
function Weave.OnRestyle(fn) end

-- ----- Extending the language ---------------------------------------------------------------

---Adds a tag: build(el, parentWidget, id) returns the IMGUI widget for <name>.
---@param name string
---@param build fun(el: WeaveElement, parent: any, id: string): any
function Weave.RegisterTag(name, build) end

---Adds a pseudo-class for selectors.
---@param name string
---@param def WeavePseudoDef
function Weave.RegisterPseudo(name, def) end

---Adds a property to the style language. Built-in names cannot be replaced.
---@param name string
---@param def WeavePropertyDef
function Weave.RegisterProperty(name, def) end

---Adds a layout mode for `display: <name>`; fn(box, items, style) sets x, y of the items.
---@param name string
---@param fn fun(box: table, items: table[], style: table)
function Weave.RegisterLayout(name, fn) end

---Names a font file (a .ttf in the game's data): then `font-family: name` works.
---@param name string
---@param path string
function Weave.RegisterFont(name, path) end

---Adds a utility-class rule. `matcher`: a whole class name ("flex") or a prefix ending in "-" ("p-").
---`rule`: declarations as text, or fn(value, ctx) returning declarations / parts / nil.
---ctx = { spacing, var(name), color(name), number(value), length(value), size(value) }.
---@param presetName string @ "wind4" is the built-in preset
---@param matcher string
---@param rule string|fun(value: string, ctx: table): string|table|nil
function Weave.RegisterUtility(presetName, matcher, rule) end

---Adds a variant (what stands before the colon in a class: `md:`, `hover:`).
---@param presetName string
---@param name string
---@param def WeaveVariantDef
function Weave.RegisterVariant(presetName, name, def) end

---Sets the width of a layout item (margins included); for authors of layout modes.
---@param item table
---@param width number
function Weave.LayoutWidth(item, width) end

-- ----- Parsers and engines (usable on their own) ----------------------------------------------

---Parses template XML. Errors carry the line number.
---@param source string
---@return { tag: string, attrs: table<string, string>, order: string[], children: table[], text: string, line: integer }|nil node
---@return string? err
function Weave.ParseXml(source) end

---Parses a style sheet.
---@param source string
---@param sheet table?
---@return table sheet
function Weave.ParseCss(source, sheet) end

---Parses a colour as written in a style sheet.
---@param value string
---@return number[]|nil @ { r, g, b, a }
function Weave.ParseColor(value) end

---Parses a length (px, vw, vh, calc(), min(), max(), clamp()).
---@param text string
---@return any
function Weave.ParseLength(text) end

---Whether a document element matches a selector.
---@param element WeaveElement|table
---@param selector table|string
---@return boolean
function Weave.Matches(element, selector) end

---A text size as written -> font units, { em = n } or nil. `base` is the overlay's text size.
---@param value string
---@param base string?
---@return number|{ em: number }|nil
function Weave.FontSizeFor(value, base) end

---The base text size in font units for an overlay text size name ("" = Medium).
---@param base string?
---@return integer
function Weave.BaseFontSize(base) end

-- ----- Expressions ---------------------------------------------------------------------------

---Compiles an expression (or statements, for events) once per text. Returns the compiled object
---or nil and why not.
---@param text string
---@return { text: string, lua: string, fn: function, statement: boolean }|nil
---@return string? err
function Weave.compile(text) end

---Evaluates an expression in a scope.
---@param expression string
---@param scope table
---@return any
function Weave.eval(expression, scope) end

---A scope: `vars` are looked up first, then `parent`.
---@param vars table?
---@param parent table?
---@param access { get: function?, has: function?, set: function? }?
---@return table
function Weave.scope(vars, parent, access) end

---Translates Weave's expression sugar (!x, &&, ?:, ++, template strings) to Lua source.
---@param text string
---@return string
function Weave.transpile(text) end

---Parses "item, i in list" for #for.
---@param text string
---@return table
function Weave.parseFor(text) end

-- ----- Reactivity ---------------------------------------------------------------------------

---A reactive view of a plain table (the same view every time for the same table).
---@generic T: table
---@param target T
---@return T
function Weave.reactive(target) end

---The plain table behind a reactive one (or the value itself): for saving, for Ext.Json.
---@generic T
---@param value T
---@return T
function Weave.raw(value) end

---Reads without subscribing.
---@generic T
---@param fn fun(...): T
---@param ... any
---@return T
function Weave.untracked(fn, ...) end

---Runs fn now and again whenever something it read changes. Returns the effect.
---@param fn fun()
---@param opts { name: string?, scheduler: fun(effect: table)? }?
---@return table effect
function Weave.effect(fn, opts) end

---Ends an effect.
---@param effect table
function Weave.stop(effect) end

---A value worked out from reactive ones, kept until one of them changes. Returns a getter.
---@generic T
---@param getter fun(): T
---@param name string?
---@return fun(): T
function Weave.computed(getter, name) end

---callback(new, old) after what source() returns has changed (not for the first value).
---@param source fun(): any
---@param callback fun(new: any, old: any)
---@param name string?
---@return table effect
function Weave.watch(source, callback, name) end

---Runs everything that is scheduled (the kit does it once per tick; tests call it).
function Weave.flush() end

---How many effects are waiting for the next flush.
---@return integer
function Weave.pending() end

-- ----- The shell: what every Weave window of every mod shares (Client/Shell.lua) ---------------

---Opens Weave's demo window (ui/demo.weave): the worked example of docs/weave.md. Toggles when `open` is nil.
---False when Weave cannot run on this Script Extender. Also `!weavedemo`.
---@param open boolean?
---@return boolean|nil
function Weave.Demo(open) end

---Opens (true), closes (false) or toggles (nil) the showcase (ui/showcase.weave): a dashboard
---of the controlled character drawn from live game data. False when Weave cannot run on this
---Script Extender. Also `!weaveshowcase`.
---@param open boolean?
---@return boolean|nil
function Weave.Showcase(open) end

---@class WeaveSettingsState
---@field theme string
---@field font string
---@field blockKeys boolean
---@field blockMouse boolean
---@field themes string[]
---@field fonts string[]
---@field saved boolean @ the settings came from the file
---@field anyOpen boolean @ Weave.Blocking()
---@field has { files: boolean }

---Settings every Weave window shares, kept in "Script Extender/Gwynerva_Weave/settings.json".
---@class WeaveSettings
Weave.Settings = {}

---A setting's value. Keys: theme, font, blockKeys, blockMouse.
---@param key string
---@return any
function Weave.Settings.Get(key) end

---Changes settings (unknown themes and fonts are ignored); `defaults = true` puts them back first.
---@param opts { theme: string?, font: string?, blockKeys: boolean?, blockMouse: boolean?, defaults: boolean? }?
---@return WeaveSettingsState
function Weave.Settings.Set(opts) end

---fn(key, value) after a setting changed.
---@param fn fun(key: string, value: any)
function Weave.Settings.OnChange(fn) end

---The settings as data, with the themes and fonts there are.
---@return WeaveSettingsState
function Weave.Settings.State() end

---True when the settings were read from the file at start.
---@return boolean
function Weave.Settings.Saved() end

---A mod offers its settings window: fn(open) opens (true), closes (false) or toggles (nil) it.
---@param fn fun(open: boolean?): boolean?
function Weave.Settings.SetWindow(fn) end

---Opens the settings window a mod offered (GateKit offers its overlay settings). False when none.
---@param open boolean?
---@return boolean
function Weave.Settings.Open(open) end

---Makes a hand-built IMGUI window take part: while isOpen() the game's input is held back, Esc
---calls close(), Weave.Rescue() calls reset().
---@param window { isOpen: fun(): boolean, close: fun(), reset: fun()? }
function Weave.RegisterWindow(window) end

---Is any Weave window (document or registered window) open?
---@return boolean
function Weave.Blocking() end

---Every Weave window back to its default place and size. Also `!weaverescue`.
---@return true
function Weave.Rescue() end

---fn sees every key before Weave blocks input; returning true claims the key (fn calls
---e:PreventAction() itself if the game should not get it). For a mod's own hotkey.
---@param fn fun(e: table, key: string, down: boolean): boolean?
function Weave.OnKey(fn) end

-- =========================================================================================
-- The renderer seam (for people writing a second renderer; not for ordinary mods)

---The renderer contract. The full list of functions is the comment at the top of
---BackendImgui.lua (windows, elements, values and events, style ops, geometry, canvas).
---@class WeaveBackend
---@field name string @ "imgui"
local Backend = {}

---nil when this renderer can draw here, or one sentence: why not.
---@return string|nil
function Backend.check() end

---What differs between set-ups that can draw at all.
---@return { files: boolean }
function Backend.capabilities() end

---Adds a tag to the renderer (Weave.RegisterTag forwards here).
---@param name string
---@param build fun(el: table, parent: any, id: string): any
function Backend.registerTag(name, build) end

---Is `<tag>` an element this renderer builds.
---@param tag string
---@return boolean
function Backend.hasTag(tag) end

-- =========================================================================================
-- What a component's <script> block sees (not global functions; provided per instance)
--
--   computed(name, fn)             a value worked out from state
--   watch(name|fn, fn(new, old))   runs after a value changed
--   effect(fn)                     runs now and again when something it read changes
--   props, state                   given properties / the component's state table
--   emit(event, ...)               calls the parent's @event
--   onMounted(fn), onUnmounted(fn) after built / before removed
--   useStorage(key, defaults)      a reactive table saved in Script Extender/Gwynerva_Weave/storage/<window>.<key>.json
--   el(selector), doc, t(key)      an element of the window; the document; the game's translation
-- Everything else resolves to `env` given to Mount, then to the globals of the Lua that mounted it.
