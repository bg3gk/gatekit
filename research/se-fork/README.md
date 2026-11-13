# se-fork — the state of the Script Extender fork work

GateKit runs on its own build of [Norbyte's Script Extender](https://github.com/Norbyte/bg3se):
a fork that lifts limits GateKit ran into, as small additive patches meant to be offerable
upstream. **The fork itself lives in its own repository,
[bg3gk/bg3se](https://github.com/bg3gk/bg3se)** — its changes are commits on a branch there, not
files here. `gatekit se install` fetches its build.

What stayed here is the working material about that fork, which belongs with the people doing
the work rather than with the code:

| | |
|---|---|
| [`upstream/`](upstream) | the changes prepared as offers to Norbyte: how one is prepared, and the first of them written out — description, commit message and where its branch is. Nothing in it has been pushed |
| [`TODO.md`](TODO.md) | what is left, written when the fork session closed: what waits on a decision of the user's, what is half done, what upstream keeps moving under it |
| [`notes.md`](notes.md) | what the fork adds, function by function, and how the game loads the extender. Was `docs/se-fork.md` in the old repository |
| [`weave-native.md`](weave-native.md) | a proposal, nothing built: what the fork would have to provide so that [Weave](../../mods/Weave) could stop drawing its own layout on ImGui's draw list |
| `build.cmd`, `setup-externals.cmd` | how the fork is built on Windows and how its dependencies are fetched and pinned. They belong in the fork's repository and should move there; they are kept so the knowledge is not lost in the meantime |
| `patches/imgui/` | the fork's change to its **ImGui dependency** (optional HDR10 output in the DX11 backend), applied by `setup-externals.cmd` to a fresh clone of `Norbyte/imgui` |

Both `.cmd` files now take the MSVC environment from `VCVARS`, or find it with `vswhere`, instead
of a path on one machine. They are Windows-only, which the rest of the repository is not: the
fork builds with MSVC and nothing else, so that is where they stay.

## The patch series is gone, deliberately

The old repository kept the fork's own changes as `.patch` files — `patches/v34/` (23 of them),
`patches/coverage/`, `patches/overlay/` and the loose `000*` series. Every one of them is now a
commit on the fork's branch, with the same message, so keeping them here would mean maintaining
two copies of the same change and watching them drift. They were not brought. Only the ImGui
diff above came along, because it is applied to somebody else's repository and is not a commit
of the fork.
