# What one access through the extender's layer costs

How much Script Extender's binding layer adds to a mod's read of a component's field, against plain
Lua, and how that stands beside one round trip of the bridge from the host. Measured in a copy on
`GustavX:Basic_Level_A`, the extender build GateKit runs, at the game's speed 1 with the window in
the background (frame cap 30).

## Claim

The layer costs tens of nanoseconds a read; a round trip of the bridge costs a tenth of a second.
Per iteration of a loop of 10^6, one loop a frame, the loop's own cost taken away, three runs on each
side:

| what the loop does | server, ns | client, ns |
|---|---|---|
| `entity.Health.Hp` (the entity cached) | 68, 69, 69 | 68, 67, 66 |
| `health.Hp` (the component cached) | 29, 28, 29 | 28, 35, 34 |
| a field of a plain Lua table | 6, 4, 6 | 5, 6, 5 |
| `Ext.Utils.MonotonicTime()` | 48, 44, 45 | 47, 47, 49 |
| the same through a local | 32, 34, 32 | 37, 32, 31 |
| a trivial Lua function | 17, 18, 17 | 24, 16, 15 |

- **The layer's price on a field** - the cached component's field minus the table's field - is
  23-29 ns; the step from the entity to the component (`entity.Health`) adds about 40 ns more. A call of an extender function costs about two calls of a Lua
  function.
- **A round trip of the bridge** (`game.burst(1, 60)` in a test, a trivial chunk, one sender,
  client and server in turn), three rounds: median 127, 126, 126 ms; p10 109 ms, p90 222 ms. Through
  the command line, `gatekit --instance <copy> exec server "return 1"` takes 0.29-0.31 s with the
  program's own start.

So one round trip of the bridge is worth about two million reads of a field through the layer.
The layer is not where a mod's or a test's time goes; the bridge's round trip is bounded by its
polling - the host looks for the answer every 100 ms (`BridgeClient`), the game reads its queue on
its tick - not by the work.

## What it stands on

- The Lua: `bench.lua` in the work notes of this measurement, reproduced here in words: each loop
  runs 10^6 times inside one `exec`, timed with `Ext.Utils.MonotonicTime()` (milliseconds, so a
  nanosecond per iteration at 10^6); an empty loop of the same length is timed first and taken
  away. One loop per `exec`, so no frame holds more than one loop (the longest, about 70 ms); the
  game did not stall or close.
- The entity is the host's character on the server and the first entity with `Health` on the
  client.
- The round trip is the test runner's `game.burst` (`src/GateKit.Testing/GameApi.cs`), run with
  `gatekit --instance <copy> run <file> --attach`.

## What is not known

- The cost in a crowded level or a fight, where the tick is longer: the reads are the same code,
  the queue is read once a tick, so the round trip would grow with the tick and the reads would not.
- A field of a nested type, an array's element, a write: not measured.
- How much of the round trip a shorter poll would remove: the host's 100 ms poll and the game's
  33 ms tick at a cap of 30 are the visible parts; nothing was changed to see.
