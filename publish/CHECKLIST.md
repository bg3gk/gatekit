# Publishing: the steps still open

What is left before GateKit is public in full, in order. Every step that leaves the machine is
taken on the owner's word.

## The history

- [ ] **The cleaned history replaces `main`.** `next` is rebuilt from the working branch as commits
      by subject and accepted only when its final tree equals the branch's and the build and tests
      are green on its tip; then it is force-pushed over `main`.
- [ ] **The map repository follows.** `bg3gk/map` holds `gatekit` as a submodule pinned to a
      commit of the old history, which stops being reachable; its pin moves to a commit of the new
      history in the same sitting.
- [ ] **A last look at what becomes public**: `git log --stat`, and the checks the `texts` job runs
      (no non-English outside `research/`, no machine paths, nothing of Larian's).

## The organisation

- [ ] **The bot is a member.** `gatekit-thrall` is invited to `bg3gk` as a Member with Write, and
      the invitation accepted as the bot ([.github/thrall.md](../.github/thrall.md)).
- [ ] **The profile and the repositories' faces**: `bg3gk/.github` holds `profile/README.md` from
      `publish/org-profile/`; each repository has its description and topics; the organisation's
      avatar is `assets/brand/logo-square.png`.
- [ ] **`bg3gk/map` has issues enabled** - the way to ask for a takedown.

## The databases

- [ ] **A release of the databases**: `gatekit index export-release` and
      `gatekit symbols export-release`, the assets uploaded to `bg3gk/map`, then its tag into
      `index-release.json`. Until then `index install` and `symbols install` refuse and point at
      the build commands.

## From a clean machine

- [ ] Clone, `dotnet build`, `gatekit doctor`, `gatekit se install`, `gatekit index install`,
      `gatekit --instance <copy> up --level GustavX:Basic_Level_A`, then a mirrored tile and a
      frame cap in a deck - both need GateKit's Script Extender build, so this is the step that
      proves the fork's release reaches a stranger.

## After publication

- [ ] **The first pull request to `Norbyte/bg3se`**, from the owner's own account: the sound
      settings mapping prepared in `research/se-fork/upstream/`.
- [ ] **Announce it where modders are**, once the above works for someone who is not us.
