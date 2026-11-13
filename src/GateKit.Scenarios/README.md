# GateKit.Scenarios

`gatekit test` and `gatekit run`: scenarios that drive a real game through the stand and the
bridge mod. A scenario is a **Lua file executed on the host**, not in the game; it reaches the
game only through the `game` object, which sends Lua chunks over the bridge.

```lua
scenario { level = "GustavX:TUT_Avernus_C", mods = {}, clean = true }

function run(game)
    game.wait_state("Running", "server", 60)
    local host = game.server("return Osi.GetHostCharacter()")
    check(host, "no host character")
    game.shot("smoke.jpg")
end
```

The repository's scenarios live in `scenarios/` (nine ported from the old Python stand, plus
`deck.lua`).

## Why Lua on the host, and why MoonSharp

The game's own scripting is Lua, the bridge speaks Lua, and most of a scenario's text is a Lua
chunk for the game; writing the host side in another language meant quoting Lua inside Python
strings. With Lua on both sides a scenario is one language throughout, and modders who write
`.weave` files or Script Extender mods need nothing new.

The interpreter is [MoonSharp](https://www.moonsharp.org/) 2.0.0 (MIT): a Lua 5.2 interpreter
written in C#, packaged for `netstandard2.0`, so it runs on `net10.0` on every platform the
stand supports with no native library and no build step. The alternatives were weighed and
dropped: NLua/KeraLua needs a native `lua54` per platform (and `gatekit` must stay
OS-neutral), `Lua-CSharp` and `MoonSharp.Interpreter.netcore` are smaller forks with less use,
and embedding Python again would have kept the two-language problem. MoonSharp has had no
release since 2017, which is the price; nothing here needs it to change, it is a single
dependency behind `Scenario.NewScript`, and swapping it would touch only `LuaValues` and
`GameApi`.

Two differences from the game's Lua follow from MoonSharp and are worth knowing when writing a
scenario (they do not touch chunks sent to the game, which the game's own Lua 5.4 reads):

- Lua 5.2 syntax on the host: no `//`, no integer division, no `goto`.
- `#text` and `string.byte` count characters, not UTF-8 bytes, and `string.byte` does not know
  characters above ASCII. Where the game counts bytes (a cursor position, for instance), the
  host helper `bytes(text)` gives the UTF-8 length. Text with non-ASCII characters is kept
  whole in literals and on the way to the game.

## What a scenario gets

`scenario { level =, mods =, save =, clean = }` at the top declares the start it needs; the
runner groups scenarios by it, so scenarios that need the same start share one game. `clean`
(or `test --clean`) gives a scenario the world as it was right after the start: one checkpoint
per group, a reset before each such scenario (about 14 s, against a 40 s restart).

`game` (the names are the old Python `Game` object's, so a Python scenario translates line by
line):

| | |
|---|---|
| `game.server(code [, timeout])`, `game.client`, `game.ui`, `game.lua(side, code)` | run a Lua chunk in the game, return what it returned |
| `game.wait(side, expression [, timeout [, every]])` | poll `return (expression)` until it is truthy |
| `game.wait_state(state [, side [, timeout]])`, `game.state(side)` | the bridge's heartbeat |
| `game.checkpoint()`, `game.reset([timeout [, skip_intro]])`, `game.reload()` | a clean world without a restart; hot reload |
| `game.skip_scenes()`, `game.fast_scenes(speed)` | play cutscenes and dialogues faster |
| `game.dialog()`, `game.answer(n)` | the dialogue on screen |
| `game.key(name)`, `game.shot(path [, width])`, `game.logs{ story =, lines =, grep = }` | a key, a screenshot, what Script Extender printed |
| `game.windows()` | the game copies running and their tiles in a deck, if one is open (`docs/design/deck.md`) |
| `game.sleep(seconds)`, `game.now()` | time |
| `game.out`, `game.path(p)`, `game.save(path, text)`, `game.read`, `game.remove`, `game.mkdir`, `game.exists`, `game.glob(folder, pattern)`, `game.testimage(path, w, h)` | files; a relative path is in this scenario's output folder |
| `game.channel`, `game.extender`, `game.weave`, `game.folder`, `game.profile` | the folders of the game this run uses (an instance's own, when there is one) |
| `game.burst(senders, each)` | several senders talking to both sides at once (the channel scenario) |

Helpers from the prelude (`Prelude.lua`, loaded before every scenario): `check(condition,
message, ...)`, `eq(got, wanted, message)`, `same`, `near`, `fmt`, `sorted`, `keys`, `values`,
`has`, `count`, `contains`, `startswith`, `endswith`, `join`, `bytes`, `lua_list`,
`lua_string`, `settled(expression)`.

A failure (a `check` that does not hold, a Lua error, a timeout, the game refusing) ends the
scenario with `FAIL` and leaves `failure.jpg` and `console.txt` in its output folder; the rest
of the suite goes on. The exit code is 0 only when every scenario passed.
