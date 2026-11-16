# The Script Extender fork

GateKit runs on its own build of [Norbyte's Script Extender](https://github.com/Norbyte/bg3se): a
fork that lifts limits GateKit ran into, as small additive changes meant to be offerable
upstream. The fork lives in its own repository, [bg3gk/bg3se](https://github.com/bg3gk/bg3se),
branches `gatekit-v34` and `gatekit-native` above it; what it adds, and how a modder uses each
addition, is its [GATEKIT.md](https://github.com/bg3gk/bg3se/blob/gatekit-native/GATEKIT.md), and
its build scripts and the patch to its ImGui dependency are in its `gatekit/` folder.

GateKit finds a checkout of the fork through `"seFork"` in `gatekit.json` (or `GATEKIT_SE_FORK`,
`BG3SE_SRC`); `gatekit doctor` says where it looked. `gatekit names generate` writes the fork's
`Coverage.h`, `gatekit symbols build` reads its headers, and `gatekit se install` fetches the
build pinned in `se-release.json`.

| | |
|---|---|
| [`upstream/`](upstream) | how a change is offered to Norbyte, and the first offer written out |
| [`weave-native.md`](weave-native.md) | a proposal, nothing built: what the fork would provide so that [Weave](../../mods/Weave) could stop laying out on ImGui's draw list |

What is open in the fork: [../open-items.md](../open-items.md), "The Script Extender fork".
