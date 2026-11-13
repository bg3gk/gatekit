# Publishing, in order

Nothing here has been done yet. It is written down so the first publication is mechanical rather
than remembered, and so the steps that only the owner can take are obvious.

## What publication waits for

The owner decided (2026-11-13) to publish once **the map site** and **the deck** are ready: they
are what someone sees first, and the deck in particular appears to be something nobody else has.

"Ready" has to mean something, or it will never arrive. It means:

- **The map site** renders all three maps, the search works in a browser, the cross-links are
  walkable, and it fits under the 1 GB a published Pages site may be. Not: every page polished,
  every kind covered, screenshots of everything.
- **The deck** shows several games without stealing focus, holds a maximized tile, mirrors at a
  rate worth watching, and **does not take hold of a game it was not given**. Not: Linux.

  The last of those is not polish. A deck today adopts every copy on the machine, caps its frame
  rate and mutes it — including one a person started for their own reasons. Someone meeting the
  deck for the first time with their own game running will see it grabbed, slowed and silenced
  without being asked, and the deck is the thing people look at first.

  (The frame budget and the sound policy were once below this line, which was a mistake worth
  remembering: every mirror measurement was of a game the engine throttles, so without the frame
  cap the deck could be demonstrated but not *measured*. A readiness line that excludes what
  makes the evidence mean anything is the wrong shape of line.)

Everything else on this list is already done or is a step at the moment of publishing. If either
of the two drifts badly, publishing the toolkit without them is still better than not publishing
— that call is the owner's.

## Before anything is pushed

- [x] **The bot account** `gatekit-thrall` exists (id 356219887) and its address is in
      `.github/thrall-identity`. Still to do by the owner: invite it to `bg3gk` as a Member with
      Write, and accept the invitation as the bot. `.github/thrall.md` has the detail.
- [x] **The migration history** was rebuilt as seven subject commits, authored by the owner with
      the bot as co-author, before anything was pushed; they are the first seven commits in the
      repository. The day of machine-paced migration commits is kept locally on the branch
      `archive/migration-history` and is not published. Everything after those seven is work done
      since, one commit per decision, and it stays that way: a project that says it is built by
      agents under review has nothing but its history to show for the claim.
- [ ] **Each repository's description and topics**, and the organisation's avatar from
      `assets/brand/logo-square.png` — the profile page is the first thing anyone sees.
- [ ] **A last look at what is about to become public**: `git log --stat`, and the three checks
      the `texts` job runs (no non-English outside `research/`, no machine paths, nothing of
      Larian's).

## The repositories

- [ ] **`bg3gk/gatekit`** — public. Push `main`. Description, topics, the organisation avatar
      from `assets/brand/logo-square.png`.
- [ ] **`bg3gk/bg3se`** — a GitHub fork of `Norbyte/bg3se`, so the relationship is visible and
      pull requests upstream are one click. Push the branch `gatekit-v34`. Its licence is
      Norbyte's and stays verbatim; `GATEKIT.md` on that branch says what the fork adds.
- [ ] **`bg3gk/map`** — public, **issues enabled** (the only way to ask for a takedown).
      Its content is `publish/map/` in this repository.
- [ ] **`bg3gk/.github`** — public, holding `profile/README.md` from `publish/org-profile/`.
      GitHub shows it on the organisation's page; without it the page is empty.
- [ ] Mods, when there are any, are named `mod-<name>`.

## Then the things that depend on a repository existing

- [ ] **A release of the fork**, with `BG3ScriptExtender.dll` zipped, and its tag, asset name and
      sha256 into `se-release.json`. Until this exists, `gatekit se install` can only say so.
- [ ] **A release of the databases**: `gatekit data export-release` and
      `gatekit symbols export-release`, upload, then the tag and manifest into
      `index-release.json`.
- [ ] **Check from a clean machine**: clone, `dotnet build`, `gatekit doctor`,
      `gatekit se install`, `gatekit data install`, `gatekit --instance x up --level …`, and
      then **a mirrored tile and a frame cap** — both need GateKit's extender build, so this is
      the step that proves the fork release actually reaches a stranger. Without it the check
      passes while the thing the deck is published for does not work.

## After publication

- [ ] **The first pull request to `Norbyte/bg3se`**, from the owner's own account: the sound
      settings mapping prepared in `research/se-fork/upstream/`. Publication comes first so he
      can look at the organisation and judge whether this is serious.

## What resumes afterwards

The two directions paused for the migration start again once the first offers have gone upstream:
**agent runs** through the game and **Script Extender coverage** of the engine. Several runs at
once, split on purpose — two playing the game as a player would, one or two working the
interface outside it: the main menu, loading saves, settings, character creation. The two halves
cover the engine from opposite sides, and coverage from one side alone keeps missing what the
other touches. The deck is what makes several at once practical.

How coverage itself is done also changes — agents that watch what comes alive and close in on
it, instead of replaying recorded tours. [docs/design/coverage-runs.md](../docs/design/coverage-runs.md).

## Later, not blocking

- [ ] The search site (`docs/design/map-site.md`), on Pages in `bg3gk/map`.
- [ ] Announce it where modders are, once the above works for someone who is not us.
