# perf — where the game's time goes

Tools that measure Baldur's Gate 3 instead of guessing about it: how long a cold start takes and
which seconds belong to whom, what an idle game costs, what the lowest graphics settings buy a
test run, and whether a world can be reset without leaving the process.

[`load-speed.md`](load-speed.md) is what they found: the start-up budget broken down, measured on
one machine on 2026-11-12, and the ideas that followed from it. It is the reason `gatekit up`
reaches a test level in about forty seconds.

| | |
|---|---|
| `startup_profile.py` | the core: starts the game N times in the `perf` instance, records process counters and the game's own log, writes a run file |
| `run_events.py`, `stack_report.py` | reading those runs back |
| `stack_sampler.py` | a sampling profiler that needs no administrator rights and no symbols |
| `idle_cost.py`, `graphics_bench.py`, `reload_bench.py`, `exists_cost.py` | the single experiments |
| `inject.py`, `lua.py` | loading a DLL into the measured game, and running Lua in it |
| `categories.json` | how sampled stacks are grouped into the names the report prints |
| `fastexists/` | a C++ prototype: the game asks Windows whether a loose file exists before opening almost any packed one, tens of thousands of times during a start. This DLL answers those questions from a cached listing. A prototype — it was measured, not adopted |

**These are Python and still need the old repository's stand**, like
[`../coverage/`](../coverage): `startup_profile.load_stand()` imports the old `stand/instances.py`
to drive an instance. Set `GATEKIT_OLD_REPO` to a checkout of it (see
[`../oldrepo.py`](../oldrepo.py)). Porting them onto the C# host is open work; the measurement
code itself does not depend on how the game is started.

`runs/` (the recorded runs) and `fastexists/out` (the built DLL) are made locally and are not
committed.
