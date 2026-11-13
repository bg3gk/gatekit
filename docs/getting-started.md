# Getting started

From nothing to your own mod's Lua reloading inside a running game. About fifteen minutes, most
of it waiting for downloads.

## What you need

- **Baldur's Gate 3**, Patch 8 (4.1.1.x), your own copy. Nothing here ships game data.
- **[.NET 10](https://dotnet.microsoft.com/download)**.
- **[Script Extender](https://github.com/Norbyte/bg3se)** installed in the game. GateKit uses its
  own build of it (below), but the stock one must be installed first.

Nothing else: GateKit packs mods itself, with LSLib built into it, so LSLib's Divine — and the
older .NET runtime it wants — is no longer needed.

Windows works today. Linux (through Proton) is written but has not yet been run on real
hardware — if you try it, tell us what broke.

## Set it up

```bash
git clone https://github.com/bg3gk/gatekit
cd gatekit
dotnet build
./gatekit doctor
```

`doctor` prints what it found and what it could not, each with the setting that fixes it. It
finds the game through Steam by itself; if it cannot, point it:

```bash
export BG3_GAME="C:/Program Files (x86)/Steam/steamapps/common/Baldurs Gate 3"
```

Anything with a `GATEKIT_` name can also live in a `gatekit.json` beside the repository; see
[contracts/cli.md](contracts/cli.md).

On Windows, run `./gatekit firewall` once as well. Every game copy is a folder of its own, and a
firewall rule is held against a path, so Windows asks "allow this program on the network?" about
each copy separately - an elevated window over whatever you were doing. The command prints the one
line that allows them all; run it in an elevated PowerShell. GateKit never changes a firewall rule
itself.

Then take GateKit's Script Extender build and pack the mods that go into the game:

```bash
./gatekit se install
./gatekit build
```

## Start a game

```bash
./gatekit --instance mine up --level GustavX:TUT_Avernus_C
```

`--instance mine` clones the game into a copy of its own — its saves, mod list and settings are
separate from yours, and you can have several at once. The first clone takes a minute; after
that the game is playable in about 40 seconds, straight in the level, with no launcher, logo or
menu.

Ask it something:

```bash
./gatekit --instance mine exec server "return GK.Party()"
./gatekit --instance mine exec server "return GK.Character(Osi.GetHostCharacter()).Stats"
./gatekit --instance mine shot party.png          # the window may stay behind others
```

`server` is the side that owns the world; `client` is what you see. [concepts.md](concepts.md)
explains why there are two.

## Your own mod, reloading live

Point the stand at your mod and let it watch:

```bash
./gatekit --instance mine link path/to/MyMod      # the folder holding Mods/<YourMod>/ScriptExtender/Lua
./gatekit --instance mine watch
```

Now edit any Lua file in your mod. About a second after you save, the game is running the new
code — no restart, no reload of the save.

## A test that proves something

Scenarios are Lua files run on your computer, not in the game:

```lua
-- scenarios/shield.lua
scenario { level = "GustavX:TUT_Avernus_C", mods = { "MyMod" }, clean = true }

local host = game.server("return Osi.GetHostCharacter()")
game.server(("Osi.TemplateAddTo('%s', '%s', 1)"):format(SHIELD, host))
assert(game.server(("return GK.Equipped('%s').Shield"):format(host)), "the shield is not equipped")
```

```bash
./gatekit --instance mine test            # every scenario, pass or fail
./gatekit --instance mine run scenarios/shield.lua
```

Scenarios that need the same start share one game. `clean` rewinds the world to how it was right
after the start — about 14 seconds, instead of a 40-second restart.

## When you are done

```bash
./gatekit --instance mine down       # closes the game and puts everything back
./gatekit --instance mine destroy    # and deletes the clone
```

`down` always restores what it changed, even after a failed run. If a game is left running,
`gatekit status` will say so.

## Where to go next

- **Windows in the game** → [Weave](../mods/Weave/README.md).
- **Driving it from an AI agent** → [driving-the-game.md](driving-the-game.md).
- **Every command and setting** → [contracts/cli.md](contracts/cli.md).
- **What the game exposes to Lua** → [contracts/lua-api.md](contracts/lua-api.md).
