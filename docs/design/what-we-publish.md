# What we publish, and what each person builds

GateKit produces three kinds of data. They look similar — all of them come out of the game — and
they are not the same thing at all. This page draws the line once, so it is not argued again
every time something new is generated.

## 1. What we worked out — published

The engine's component layouts, field offsets, the names a field really deserves, what a system
does, which component appears on what kind of entity, timings, the coverage numbers. None of it
exists in any file Larian ships: it is the result of reading, measuring and checking in a running
game, and writing down what was found.

This is ours, the way a map of a coastline belongs to whoever surveyed it. It is in the
repository (`research/coverage/`), and it stays there. Keeping it private would also be
pointless unkindness: every modder who wants it would otherwise spend the same weeks finding the
same facts.

Norbyte's Script Extender does exactly this — it ships the structures and names of the engine it
has mapped — and it is the foundation the whole BG3 modding scene stands on.

The identifiers of the game appear inside these findings, because a finding like "this component
is on the entities of that dialogue" cannot be written without naming the dialogue. Names used
to say what we found are part of the finding.

## 2. What the game ships — published separately, and withdrawn on request

A full index of the game's own content — every dialogue line, stat, template and localized
string — read out of the `.pak` files into one database. It is the game's own material,
rearranged so it can be queried.

The owner of this project, [@Gwynerva](https://github.com/Gwynerva), has decided to publish it
and accepts the risk of doing so, for a plain reason: every modder and every agent that wants to
answer a question about the game otherwise builds the same database from the same files, and a
shared one is simply better for everyone. It is the same bargain the scene already lives by, and
the precedent is not a vague one: Norbyte, whose Script Extender everything here is built on,
has run a searchable index of the game's own data for years, and the shape of our map site is
copied from his. The community's other search sites and name lists exist for the same reason.

Two conditions, and they are not decoration:

- **It lives in its own repository**, [bg3gk/map](https://github.com/bg3gk/map), and not in this
  one. Separate means it can be taken down on its own, without touching the tools. One
  repository holds all three maps — the game's content, the engine's symbols, and our component
  map — because splitting them across three would only make them harder to find and no easier to
  withdraw.
- **It is withdrawn the same day it is asked for**, by Larian or by anyone acting for them. Its
  README says what to do in three sentences, which is as much as that needs.

### Why it travels as release assets and not in the tree

It is tempting to commit the database and be done with it. GitHub's own limits say otherwise,
and the reasons are worth writing down so this is not reopened:

| | |
|---|---|
| a single file in a repository | **blocked above 100 MiB** — a 3.5 GB database cannot be committed at all, and 0.9 GB compressed cannot either |
| a repository as a whole | ideally under 1 GB, "less than 5 GB strongly recommended"; no hard maximum, but support may ask you to fix it |
| **a release asset** | **under 2 GiB each**, up to 1000 per release, and **no limit on total size or on bandwidth** |
| Git LFS | 2 GB per file on the free plans, metered storage and bandwidth, and **it cannot serve GitHub Pages** |
| a published Pages site | no larger than 1 GB |

Splitting the database into forty files under 100 MiB would get it into a repository, and that
is the wrong trade. Everything ever committed stays in the history: every clone would pull all
of it forever, each rebuilt index for a new patch would add another copy, and taking it down
would mean rewriting history rather than pressing delete. A release asset is one file, replaced
per patch, removed in a moment — which is the whole point of the promise above.

So: about 3.5 GB, 0.9 GB compressed with zstd, as a release asset with a checksum, fetched by
`gatekit data install`. Building it yourself stays a first-class path and takes 175 s, so nobody
is forced through a download.

How that works in this repository: `gatekit data export-release` and `gatekit symbols
export-release` compress a built database with Zstandard, cut it into parts small enough to
upload and write a `manifest.json` saying what each one weighs, what it hashes to and which build
of the game it was read from; `index-release.json` at the root pins the release the install
commands read, the way `se-release.json` pins the Script Extender build; and `publish/map/`
holds what the separate repository will contain, withdrawal promise first. See
[docs/contracts/cli.md](../contracts/cli.md), section 14.3.

Published there: the game-data index, the symbol database (names, addresses, sizes, layouts, the
call graph and the string literals of the engine's binaries — facts about the code) and the
component map from `research/coverage/`. The same repository also serves the search site, a
reduced extract small enough for GitHub Pages — see [map-site.md](map-site.md).

**Why no server.** A machine of our own would lift every size limit, and that is the wrong trade
for this project: it costs money every month, it has to be kept patched, it goes down when
nobody is watching, and it is a thing to attack. A static site someone else hosts for free
outlives our attention, and it is the easiest thing in the world to take down when asked — which
is a promise this project has made. If the site ever will not fit under the 1 GB a published
Pages site may be, the answer is to carry less on it, not to rent a server.

## 3. The code itself — never published

Decompiled pseudo-code from the game's executables, and anything that is a transcription of
compiled code. GateKit ships the pipeline that produces it; what it produces stays on the
machine that produced it. This is the line the owner drew explicitly, and it does not move.

Names, addresses, sizes, layouts, call edges and string literals are *facts about* that code,
not the code, and belong to the kinds above.

## In practice

- `research/` holds findings, and may hold the identifiers needed to express them.
- A built database goes in `.gitignore` here, and either a command builds it in minutes or
  `bg3gk/map` has it ready.
- Anything large travels as a release asset with a checksum, never in the tree — assets are easy
  to withdraw, which is the point.
- **If Larian ever asks us to take something down, we take it down the same day and say so.**
  This project exists because of their game; being easy to deal with costs nothing and is the
  only reason any of this is tolerated.

Nothing here is legal advice, and none of it has been tested against Larian's terms by anyone
qualified. It is the line the project draws for itself, with its owner accepting what section 2
carries.
