# Getting started

From a clone of the repository to a test of your own and your mod's Lua reloading in a running
copy of the game. What you need is listed in the [README](../README.md); every command and option
is in [contracts/cli.md](contracts/cli.md).

## Install and check

```bash
git clone https://github.com/bg3gk/gatekit
cd gatekit
./gatekit doctor
```

`./gatekit` builds the toolkit the first time it runs (into `artifacts/gatekit`, and again in the
background after a `dotnet build`: [contracts/cli.md](contracts/cli.md#calls-how-often-the-program-is-called)). `doctor` prints what it found and what it
did not, each with the setting that fixes it. It finds the game in your Steam library; if it
cannot, set `BG3_GAME` to the game's folder.

## The home

GateKit keeps its databases, builds and state in one folder, the home. Name it in a
`gatekit.json` in a folder above your checkout, so every checkout below shares it:

```json
{ "home": "gatekit-home" }
```

A relative path is relative to that file. Without one, the home is a per-user default and
`doctor` says it is not configured. The layout and every other setting: [design/home.md](design/home.md).

## Script Extender and the firewall

```bash
./gatekit se install
```

fetches GateKit's build of Script Extender into the home and makes it the build every copy
starts with; it writes into no game. On Windows, `gatekit firewall` lists the copies the firewall
does not yet let listen and prints the one line that allows them; GateKit never changes a firewall
rule itself ([contracts/cli.md](contracts/cli.md)).

## Your first copy

```bash
./gatekit --instance mine up --level GustavX:Basic_Level_A
```

`--instance mine` makes a copy of the game named `mine` on its first use and starts it, bare (the
base game and GateKit's mods), straight into the sandbox level: no launcher, no logo, no menu.
What a copy is: [concepts.md](concepts.md#copies). Ask it something:

```bash
./gatekit --instance mine exec server "return GK.Party()"
./gatekit --instance mine shot party.png       # the window may stay behind others
./gatekit game verify                          # the installed game, file by file
```

A command that writes never falls back to the installed game: without `--instance` it refuses in
one sentence, unless the installed game is named with `--installed-game` (then `up` backs up your
saves and mod list and `down` puts them back). Reading - `status`, `doctor`, `game verify` - needs
neither.

`server` is the side that owns the world, `client` what is shown.

## Your first test

A test is a Lua file run by the host; it declares where it runs and does its work in named steps,
each of a class:

```lua
-- tests/mine/party.lua
test { level = "GustavX:Basic_Level_A" }

function run(game)
    local party = step("read the party", "observation", function()
        return game.server("return GK.Party()")
    end)
    check(#party > 0, "the party has a member", party)
end
```

```bash
./gatekit --instance mine test tests -k mine/party
```

A copy already running in the declared level is used as it is. How to write tests that are worth
having: [writing-tests.md](writing-tests.md).

## Your mod, reloading live

```bash
./gatekit --instance mine link path/to/MyMod   # the folder holding Mods/<YourMod>/ScriptExtender/Lua
./gatekit --instance mine watch
```

Edit a Lua file of your mod and save it: the running game restarts its Lua in place with the new
code, with no restart and no reload of the save. `reload` does it once; `bridge reload` does the
same for GateKit's own mod.

## When you are done

```bash
./gatekit --instance mine down       # close the game
./gatekit --instance mine destroy    # delete the copy
```

## Next

- Driving the game from an AI agent → [driving-the-game.md](driving-the-game.md)
- An AI agent that plays: GateKit AI's four tools → [mods/AI](../mods/AI/README.md)
- The databases of the game's data and code → [offline-databases.md](offline-databases.md)
- In-game windows → [Weave](../mods/Weave/README.md)
- The API inside the game → [contracts/lua-api.md](contracts/lua-api.md)
- What GateKit's Script Extender adds for any mod → its [GATEKIT.md](https://github.com/bg3gk/bg3se/blob/gatekit-native/GATEKIT.md)
