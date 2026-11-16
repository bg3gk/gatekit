# Contributing

Issues, questions and patches are welcome. [AGENTS.md](AGENTS.md) is the map of the repository
and its rules, for people and agents alike; this page adds what a contributor needs on top of it.

## Language

English is the language of everything in this repository (AGENTS rule 1), with no exceptions:

- **Localization**: text meant to be shown in many languages comes from the game's own data
  (localization handles, stat ids), never a table written in the repository.
- **Test data**: a test that needs characters outside ASCII writes them as escapes (`"\u{43C}"`
  in Lua, a `(char)0x43C` in C#), so the file itself stays English.
- **Quotes**: text the game showed in another language is quoted in English translation, saying
  which language it was shown in.

Continuous integration fails on any non-English text, in every file, `research/` included.

## Repositories

In the `bg3gk` organisation: `gatekit` is the toolkit, `bg3se` keeps its upstream name because it
is a fork of it, `map` holds the prebuilt data, and a published mod is `mod-<name>`.

## Agent-written patches

Welcome, on the same terms as any other: you are the author of what you send. Understand your
patch and run it against a real game before opening a pull request. Co-authorship and the bot's
signature: [.github/thrall.md](.github/thrall.md).

## Before you open a pull request

- `dotnet build` and `dotnet test` pass.
- The tests pass in a game copy (`gatekit --instance <yours> test tests`), or you say which ones
  you could not run and why.
- The contract in `docs/contracts/` matches the behaviour you changed, in the same commit.
- AGENTS rules 3, 4 and 5 hold: nothing of Larian's, nothing Windows-only outside
  `src/GateKit.Platform`, no machine-specific paths.

A change of the API inside the game or a new test may be sent complete and ready; it is merged
into `main` only with the explicit consent of the repository's owners (AGENTS rule 16).

Conduct: [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md). Something that could harm a user's machine or
game: report it privately, as [SECURITY.md](SECURITY.md) says.
