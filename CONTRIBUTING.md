# Contributing

GateKit is in active development and is not production-ready; interfaces change without notice.
Issues, questions and patches are welcome anyway. [AGENTS.md](AGENTS.md) is the short map of the
repository — read it first, whether you are a person or an agent.

## Language

English is the language of everything in this repository: code, identifiers, comments,
documentation, TODO lists, journals, commit messages, scenario names and messages.

Exceptions, all deliberate:

- **Localization**: text meant to be shown in many languages comes from the game's own data
  (localization handles, stat ids) or from translation files made for that purpose — never a
  hardcoded table in one language.
- **Test data**: non-ASCII strings that test text handling stay as they are; that is what they
  test.
- **Quotes**: names or text quoted verbatim from the game or from another source.

A file that needs one of these exceptions says so in itself, on a line of its own:

```
gatekit:allow-non-english  the game's own strings, to test that they survive the channel
```

Continuous integration fails on non-English text in any other file. `research/` is working
material and is exempt; see its README.

## Names

Repositories in the `bg3gk` organisation: `gatekit` is the toolkit, `bg3se` keeps its upstream
name because it is a fork of it, `map` holds the prebuilt data, and a published mod is
`mod-<name>`.

Name things as the game does: where the game has its own name for a field or function, that name
wins, and other names (the Script Extender author's included) stay as aliases.

## What is never committed

Anything of Larian's: game data, binaries, decompiled code, fonts, textures. Every tool here
builds such data on the user's own machine from their own copy of the game. Facts we worked out
ourselves — offsets, layouts, measurements — are ours and may be written down.

## AI-written changes

Most of this repository is written by AI agents under human review, and we say so in the README
rather than hiding it. You may do the same: agent-written patches are welcome.

The rule is the same as for anyone: **you are the author of what you send.** Understand your
patch, and run it against a real game (`gatekit --instance <yours> test`) before opening a pull
request — not only `dotnet test`. Commits written by an agent keep their `Co-Authored-By` line;
it is a note about how the work was done, not a transfer of responsibility.

## Who signs what

This project speaks under the name **GateKit Thrall** in pull requests, commits, issues and
discussions. It is a voice, not an autonomous bot: agents draft the text, and a person chooses
what is said and posts it. Everything published under that name ends with one line, so nobody
has to guess whether they are reading a person or a machine; everything you write in your own
name does not carry it. The exact wording is in [.github/thrall.md](.github/thrall.md).

### Co-authorship

No AI assistant is a co-author under its own name — whichever one you used, the trailer is
`Co-Authored-By: GateKit Thrall <356219887+gatekit-thrall@users.noreply.github.com>`, the name this project speaks under. The hook writes it
for you (`git config core.hooksPath .githooks`), and continuous integration refuses commits that
name an assistant. A commit you wrote alone needs no co-author.

## Before you open a pull request

- `dotnet build` and `dotnet test` pass.
- The scenarios pass against a real game, or you say which ones you could not run and why.
- The contract in `docs/contracts/` matches the behaviour you changed, in the same commit.
- No machine-specific paths, nothing of Larian's, nothing Windows-only outside
  `src/GateKit.Platform`.

## Conduct

Be civil, and read [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) — it is short, and honest about what
one person can enforce.

## Security

Found something that could harm a user's machine or their game installation? Please report it
privately: see [SECURITY.md](SECURITY.md).
