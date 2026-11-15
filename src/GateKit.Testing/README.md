# GateKit.Testing

The runner of `gatekit test`: it finds the tests in a tree, groups them by venue, prepares each
with the reset ladder, runs its Lua on the host in an embedded interpreter (MoonSharp) and reaches
the game through the `game` object. How a test is written, its header, steps and the `game` API:
[docs/writing-tests.md](../../docs/writing-tests.md). The command: [docs/contracts/cli.md](../../docs/contracts/cli.md), `test`.
