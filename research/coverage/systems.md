# Describing the systems

A component says what the engine remembers; a system says what it does. Each of the game's ECS
systems has one description file, `systems/<namespace with :: as __>/<Name>.json`, in two layers
kept apart because they are worth different things.

**What the symbols say - a fact.** The key `symbols`: the components the system's methods take as
read-only and as writable views, and its methods with the address each was found at
(`bg3_dx11.exe` by default). `gatekit symbols describe-systems` writes it and keeps every other key
of a file that exists.

**What it does - reasoning, with its evidence.** The key `does`, written by hand: a list of
claims `{"method", "text", "evidence"}`. A claim says when the method runs, what it decides,
which component it creates or removes and under what condition, in two or three sentences, and
rests on code - the pseudo-code read (`gatekit decomp code`, `gatekit data query hook`) with the
binary and the address. What could not be worked out is written as unknown: a plausible account
of a system is worse than none. Nothing of the game's code goes into the file; it is read, never
pasted.

**The wiki is context, never evidence.** It says what a system is for and in what order the game
resolves a mechanic, which helps a reading and catches a misreading. A description may carry
`context`: `{"wiki": "<page title>", "rev": <revision>, "says": "<one line in our own words>",
"agreement": "agrees" | "differs" | "silent"}` (`gatekit wiki rules` gives the revision). No
`evidence` cites the wiki, its text is never copied, and a `differs` is a lead of its own.

**A claim the game can check.** A claim becomes checkable with one more key:

```json
"check": {"when": "<a recipe in scenarios/coverage, without .lua>",
          "expect": [{"appears": {...}}, {"gone": {...}}, {"equals": {...}}, {"changes": {...}}]}
```

Four verbs and no more: a component appears, a component is gone, a field (by its name in the
layout) equals a value, a field changes. A claim that cannot be said that way says
`"check": "not checkable live: <why>"` and stays prose. `gatekit --instance <copy> systems check
<system>` replays each recipe with the expectations watched every tick and writes the verdict
beside the claim (`passed`, `failed`, `last`, and the latest cases); `gatekit systems tally`
counts claims written, checked, passed and failed. A pass proves the claim on the cases run and
nothing wider. The verbs' arguments: [docs/contracts/cli.md](../../docs/contracts/cli.md#claims-the-game-checks-systems-tally-systems-check-the-preludes-expect).

**The order.** The systems that create a component never seen alive come first
(`gatekit symbols creators <type>`), then the systems of the namespaces being worked in, then the
rest, a namespace at a time.
