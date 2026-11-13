# Security

GateKit starts processes, writes into your game's folder and profile, and runs Lua you or an
agent hand it. It is a development tool, not a sandbox.

**What to report privately**: anything that could damage a user's machine, their game
installation or their saves beyond what a development tool is expected to do — for example a
path that escapes an instance and writes into the installed game, a backup that cannot be
restored, a command that executes content fetched from the network, or a way for a mod under
test to reach outside the game.

**How**: open a [private vulnerability report](https://github.com/bg3gk/gatekit/security/advisories/new)
on GitHub. If that is not available to you, open an issue saying only that you have something to
report, with no details, and ask for a way to send them.

Please do not open a public issue with the details first.

**What is not a vulnerability**: that `gatekit exec` runs arbitrary Lua in the game (that is the
point), that the MCP server lets a connected agent do the same (likewise), or that the stand can
delete the saves of an instance it owns.

GateKit has no security releases yet; fixes land on `main`.
