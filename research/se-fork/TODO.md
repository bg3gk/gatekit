# Script Extender fork: what is left (written 2026-11-12, when the fork session closed)

State at closing: branch `gatekit-v34` in `se-fork\bg3se-v34`, 23 commits on top of upstream's
`imgui_v1_93` at `2ea16d51` (tag `base/v34-imgui_v1_93-2ea16d51`). Tested build in
`se-fork\builds\v34`, loaded by the stand by default. `se_fork` and `se_fork_ui` pass on it.
Report and API table: `research\se-fork\notes.md`. Nothing was pushed, no pull request was opened.

Rules that apply to all of it: the game's own names and data come first (old Script Extender
names stay as `[[bg3::legacy(...)]]` aliases); a layout is changed only after a check in the
running game; each change is one commit with a description fit for a pull request; a DLL goes to
`se-fork\builds\v34` only after `se_fork` and `se_fork_ui` passed on it, built in the normal
mode; workers and subagents run on Opus or cheaper.

## Waiting for the user's decision

- **Element engine of our own (`Ext.UI2`)** — retained node tree, layout, paint and events in
  C++ on ImGui's draw list. Sketch in the sessions' shared notes, `weave-needs-from-se-fork.md`,
  item 4; it asks to be discussed before any code. Large.
- **Network access for mods, gated** — same note, item 5. A security risk, not acceptable
  upstream; only on the user's explicit word.
- **Keyboard / controller UI switch (7c)** — `ecl::EocClient::SetControllerMode` /
  `SetKeyboardMode` located in the shipping exe by pattern (`symbols\requests.md`), added to
  `BinaryMappings.xml`, called on the client thread. High risk: a wrong pattern or context
  crashes, and it breaks first on a game patch. Only on the user's explicit yes.
- **Proposals to the author** — whether and when to offer patches upstream, under the user's
  GitHub account. Each one a small topic branch cut from his branch.

## Names and layouts (the user's rule: follow the game)

**Update 2026-11-12, late (coverage session, which now keeps this branch):** `gatekit-v34` is at
`f84682c8` (the coverage batch with 13 game-named layouts, fast-forward); `se-forkuilds34`
is built from it and `se_fork` / `se_fork_ui` pass on it. Live facts for the items below are
in `research\coverage\work\suspects.json` and `research\coverage\se-coverage.md`:

- `eoc::PickingStateComponent` holds data (server: 255 on 277 entities, 2 on 30, 0 on 5): the
  tag declaration is wrong. Not changed yet - the byte's name in the game is not known.
- `eoc::ActiveComponent` behaves as a tag (server all 0, client garbage): its field is
  meaningless. Not changed yet (removing a field breaks Lua that reads it; needs a decision).
- `eoc::CanTriggerRandomCastsComponent`, `esv::IsMarkedForDeletionComponent`: 312 holders each,
  all 0 on the server - data not shown live; leave. The other fourteen suspects were not
  alive in the situations available.
- `eoc::spell::LearnedSpellsComponent`: 112 bytes, all zero in 8 instances; the size fits a
  64-byte map at 0 and a 48-byte set at 64, i.e. the game's two members. "No member there"
  was most likely the symbol base not having computed offsets for this struct - read the
  header before changing anything.
- `eoc::progression::MetaComponent`: +0x30 holds 1..5 (looks like the class level), +0x40..+0x7F
  is an empty 64-byte container (the game's `ProgressionOverrides`). `ClassLevel` does not
  belong at +0x40: check where the header really puts it.
- `eoc::DifficultyCheckComponent`: four (pointer, capacity, size) triples at +0x00, +0x10,
  +0x20, +0x30, then two dwords; the first is the game's `SpellDifficultyCheckByAbility`.
- `collect.py` lost no marks: `verified` is carried over by name and side (161 types now).

- Three fields that already carry one legacy name and need a second alias by hand:
  `eoc::DifficultyCheckComponent.AbilityDC` (game: `SpellDifficultyCheckByAbility`),
  `eoc::progression::MetaComponent.ClassLevel` (game: `ProgressionOverrides`; the types look
  different — check the layout first), `eoc::sight::EntityViewshedComponent.Viewshed` (game:
  `VisibleEntityGuids`). `P_RENAMED` takes one old name; a second needs a small extension.
- `eoc::spell::LearnedSpellsComponent`: the game has `SpellsLearnedByClass` at offset 0 and
  `SavantSchools` at 64; the layout has no member there. The layout itself is suspect.
- Ten types declared as tags that hold a byte, and ten one-byte components that are empty
  (`research\coverage\leads.md`): confirm each on live entities, find the game's name of the
  byte, then fix the author's layouts. Only `eoc::PickingStateComponent` is confirmed.
- `CombatCameraParams` float order; `TLLookAtAutomationConfig::EyeLookAtBone` declared
  `uint8_t` where the code copies a FixedString (same file).
- Rerun `research\coverage\apply_game_names.py se-fork\bg3se-v34 --apply` whenever
  `research\coverage\work\fieldmeta-check.json` grows; 85 fields there are still "game name
  ambiguous" and 38 "not in Script Extender".
- Asked of the coverage session, not answered when this session closed: confirmation of the
  cases above, and that `collect.py`'s rewrite of `coverage.json` lost none of its marks.

## Features not done

- **Timeline event keys (7b)** — `TLKeyBaseComponent::Keys` is `[[bg3::hidden]]` in
  `GameDefinitions\AllSpark.h` while the key structs are declared; check each key layout hot
  with `Ext.Debug.ReadMemory`, then un-hide. The coverage session added timeline event
  components; see whether that already covers what the weapon-visibility mod needs.
- **HDR colours on Vulkan** — the conversion exists for DX11 only (pixel shader in the imgui
  backend); the same maths for the Vulkan backend.
- **Image files on Vulkan** — `RenderingBackend::CreateTexture` is implemented for DX11 only
  (needs image, view, sampler and descriptor set on Vulkan). Weave enables image files only when
  `Ext.IMGUI.GetBackendInfo().Renderer == "DX11"`.
- **`.dds` image files** — only png, jpg, tga and bmp are decoded (stb_image); a DDS reader for
  block-compressed formats, or loading through the game's texture manager.
- **Title-bar-only dragging per window** (`window.MoveFromTitleBarOnly`) — Weave's small request.
- **Docking** — not available on ImGui 1.91.9b; not looked at on the 1.93 base.
- **How the HDR result looks on the HDR display itself** was never judged by eye; the default
  paper white of 200 nits is a guess. `Ext.IMGUI.SetPaperWhite(nits)` changes it.
- **Injected mouse clicks into the game's own UI** in the background: not verified (clicks on
  IMGUI windows are).

## Housekeeping

- **Split commit 7** ("IMGUI: decorations, canvas, element geometry, removable styles") into
  four before it is offered upstream; it bundles four capabilities.
- **`se-fork\setup-externals.cmd` is still the v33 script.** For v34 bcdec, FastLZ, lz4 and stb
  were cloned by hand and ImGui was checked out at `0c44880d` in `se-fork\bg3se-v34\External\imgui`
  with `se-fork\patches\imgui\0001-...-imgui-1.93.diff` applied. Rewrite the script so a clean
  machine can reproduce the v34 tree (junctions included).
- **Keeping up with upstream**: `git fetch`, `git rebase origin/imgui_v1_93` (or `origin/main`
  once merged) in `se-fork\bg3se-v34`, rebuild, scenarios, refresh `se-fork\builds\v34` and
  `se-fork\patches\v34`. Upstream changes that branch almost daily; not done since `2ea16d51`.
- **Build speed for the coverage batches**: the fast mode (`build.cmd <tree> fast`) saves little
  there because `Components\Coverage.h` is included almost everywhere (150 s per change).
  A real gain needs the new layouts in a translation unit of their own. Offered to the
  coverage session, not started.
- **Old trees**: `se-fork\bg3se` (branch `devkit`) and `se-fork\bg3se-overlay` (branch `overlay`)
  are retired but still on disk; `se-fork\bg3se\External` holds the dependencies every tree
  links to, so `se-fork\bg3se` must stay until the dependencies are moved. Tags
  `archive/devkit-v33`, `archive/overlay-v33` keep the old branches.
- **Old instance root**: my stand instance `sefork` in `...\Baldurs Gate 3 DevKit\` is dead
  weight after the rename; a new one was created in `...\Baldurs Gate 3 GateKit\`. The old root
  can go once every session confirmed its instances.
- **`research\se-fork\notes.md`**: the sections "How the game loads Script Extender", "Risks" and the
  "History" assessment were written for v33 and only partly brought up to date; the patch table
  still numbers patches 1-11 and does not list `FontSize`, the game-name renames or the
  `VMCallEntry` fix as rows.
- **`stand\scenarios\se_fork.py` / `se_fork_ui.py`** still carry the "skipped on stock" branches;
  stock v33 is no longer supported, they can be simplified.
- **Coverage index**: `research\coverage\se-coverage.md` and `docs\plan.md` describe the v33
  measurement in their prose; the numbers were recollected on v34 (components 57.8 % by the
  coverage session's last count). Systems are still 85 of 934.
- **Bridge and console** do not use all of the fork yet (other sessions' files): Osiris name
  completion from `Ext.Osiris.GetFunctions()`, C++-side messages from
  `Ext.Events.ConsoleMessage`, `OnHistory` / `OnCompletion` / `CursorPos` for the input line,
  `SetClipboardText` for Copy, `LastScroll` for Follow, real time stamps.

## Suspect layouts of the author's, found by coverage workers on 2026-11-13 (not verified live)

- `LevelUpData` (Progression.h): on Windows the element is 0xE0 bytes with Spells at +0xD0
  (SE: 0xC0 with Spells at +0xB0); inside it LevelUpUpgrades is 0x70 bytes. Seen in
  esv::character_creation::DebugFullDefinitionRequestOneFrameComponent (same memory as SE's
  FullRespecDefinition). Evidence in research/coverage/layouts/dead/.
- `RollAdjustments.FixedRollBonuses`: the element is eoc::interrupt::RollAdjustmentMetadata,
  20 bytes, not SE's 24-byte FixedRollBonus (Toolkit copy). Seen in
  esv::interrupt::ConditionalRollAdjustmentOneFrameComponent.
- `GameTime` (ExposedTypes.h): +0x10 is read by the game as a 32-bit counter, SE declares
  `double Unknown` there (ecl::activation::CulledComponent).
- `bg3se::CameraBlockerTrigger`: the client class holds only Entity after the Trigger base;
  SE's Array at +0x70 does not fit the client constructor.
- `eoc::controller::NudgeDataComponent` (our own layout, replicated list): +0/+4 are two floats
  (the vec2 that Nudge copies), +8 the flag that triggers the copy - fix before the next build.
- `esv::spell_cast::CastStartRequest` (SpellCast.h): the game's
  esv::spell_cast::random::StartSpellCastRequestOneFrameComponent has this struct's layout up to
  the byte SE calls field_A8 (at +0xB8 on Windows) and ends at 0xC0; SE's struct carries one more
  Guid RequestGuid (0xD0). Either the struct is longer than the component or SE's is outdated -
  not settled (creator bg3.exe+0x3335FE0, destructor bg3.exe+0x22D6020).
- `UGCModDependency` (Module.h, 32 bytes: uint64 + one STDString): the Windows array copier
  bg3.exe+0x38BADA0 and destructor bg3.exe+0x38BB690 use 0x50-byte elements - a u64 plus three
  STDStrings (seen in ls::ugc::ModDependenciesResultOneFrameComponent).
- `GameTime` (above): second, independent evidence from the decompilation session - the Toolkit
  constructor ls::GameTime::GameTime(double, float, unsigned int) (CoreLib.dll+0x872F0) writes a
  double at +0, a float at +8, zero (4 bytes) at +0xC and the unsigned int at +0x10; the default
  constructor (CoreLib.dll+0x385D0) zeroes only 4 bytes at +0x10. So +0x10 is a 32-bit unsigned,
  data ends at 0x14 (size 0x18 with alignment). Two code sources agree; a live read of a GameTime
  is still wanted before the author's layout is changed.
- `ecl::PreviewEffectMap` (CharacterTask.h): the bool at +0x18 is called `Updating`; the Toolkit
  accessor names it `IsBeginCalled` (game offset 0x18). By the game-names rule the author's name
  becomes a legacy alias - a rename, not a layout change.
- `DeathData` (Components/Death.h) against the savegame model death::v4::DeathData and the Windows
  converter bg3.exe+0x2775340 - names, not layout: SE `InstantKill` (+0x23) is the game's
  AllowDownedOnDeath; SE `Chasm` (+0x2C) is InstantDeath; SE `ChasmPosition` (+0x64, optional) is
  SourcePos with SourcePosPresent at +0x70; unnamed field_48 is ImpactIsOnWeapon, field_60/61/62
  are GenerateTreasure, WasRequest, WasForced. By the game-names rule these become the primary
  names and the author's stay as aliases. NOTE: InstantKill -> AllowDownedOnDeath and Chasm ->
  InstantDeath change the MEANING a modder reads into the field - confirm live before renaming.
- `ExternalCommunityUserID` (Net.h, 16 bytes: uint32, uint8, uint64): Windows bg3.exe+0x2F38A90
  copies 24 bytes - a kind dword at +0, a byte at +4, and at +8 a payload of 8 bytes (kind 0) or
  16 bytes (kind 1). SE's struct is 8 bytes too short.

### Notes from the second pass (2026-11-13, batch 6) - check live before changing anything
- `SpellUnprepareInfo` (the author's Spell.h): the byte is named `field_20` but lies at +0x30 (after the 48-byte SpellMetaId). The layout is right, the name misleads.
- `ScratchString` (CoreLib): the constructor in `esv::SaveWorldRequestComponent` writes -1 to the third dword (the author's `Size`) and to FloatPrecision, and the destructor frees the buffer depending on the byte named `CanGrow`. The order or the names of the members are in doubt.
- `AnubisSavegameReader` in our Coverage.h: the first 0x88 bytes match the Writer / `ls::anubis::savegame::StateComponent` - the container names found there can be carried over.
- `UGCModDependency` (the author's Module.h): {u64, one STDString}; in this build the element is 0x50 bytes - a u64 and three STDStrings (a name and two thumbnail addresses). Confirmed by the second-pass worker (batch 10) from the Windows copy function bg3.exe+0x3BDF700; not checked live.
- `eoc::spell_cast::StateComponent` - the suspicion of batch 9 is CLEARED by a live check on 2026-11-13 (`author_check.py`, the Nautiloid, the Dash spell): SE returns `SpellCastGuid` exactly from the bytes at +0xA0, `Random` from +0x98, `NetGuid` - an empty string at +0xB0; the author's layout is right, the worker misread the offsets.
- Batch 12 of the second pass (from code, not checked live): `GameTime` - the author has Ticks (int) at +0xC and a double at +0x10, while the Toolkit's constructor `ls::GameTime(double, float, unsigned int)` puts the tick counter at +0x10 (a third independent source for the suspicion already recorded); `TLPreviewDummy` +0x88 - the author has a hidden `void* field_88`, the Toolkit calls it `SourceObject` (EntityHandle); `ScreenFadeCreationData` +0x14 is an int for the author, the game writes a float duration there; `ClearScreenFadeData` +0x10 (`FadeSpeed`) is filled with a duration too.

### Checked live on 2026-11-13 (`author_check.py`, the Nautiloid; five deaths: Osi.Die with the four combinations of "treasure" / "immediately", and a death from damage)
- `DeathData`: CONFIRMED - `Chasm` repeats the "immediately" argument (it is `InstantDeath`), `field_60` repeats "generate treasure" (`GenerateTreasure`), `field_61`/`field_62` are 1 for a requested death and 0 for a death from damage (`WasRequest`, `WasForced` - not told apart from each other), `ChasmPosition` is filled by Osi.Die on level ground (`SourcePos`). The renames are in the fork's working tree with the old names as aliases; they go into the next build.
- `DeathData.InstantKill` -> `AllowDownedOnDeath`: NOT confirmed and not refuted - the field is 1 in all five goblin deaths. Needs the death of a party member with and without going through the downed state. The author's name stays for now.
- `GameTime`: CONFIRMED live (a subscription to Ext.Events.Tick, both sides, 4 ticks): at +0x10 lies a 32-bit counter growing by exactly 1 per tick; the upper 4 bytes of the "double Unknown" are garbage. Beyond what was expected: at +0xC there is neither zero nor Ticks but a float equal to DeltaTime (the author read it as an int - hence numbers like 1023969408). Fixed in the fork's working tree: `float field_C`, `uint32_t Ticks` at +0x10, `uint32_t field_14`; the name `Ticks` now returns the real counter, the `Unknown` property is gone (SE's own code did not use it).
- `LevelUpData`: the suspicion is CLEARED by a live check on 2026-11-13. After a level-up from code the host has two elements in `eoc::progression::LevelUpComponent`; in raw memory the class of the second element lies at +0xE0, and SE returns the same in `LevelUps[2].Class` - the author's structure is already 0xE0 bytes (Spells at +0xD0). The worker who counted 0xC0 was wrong.
- `GlobalSwitches` +0x40: the author's `AnimationBlueprintSpeedScale_M`; it is the time multiplier of the whole game. Sources: the Toolkit's `BaseApp::SetDeltaTimeScale` and the console command `deltaTimeScale` write this field; three places in bg3.exe multiply the frame delta by it (server +0x5E5D85, client +0x3F7A2CA, UI +0x121129E); the Speed Mod (Nexus 1536) wrote the same field in 2023 (then at +0x5C). Live: 4 and 10 speed timers and fights up 4 and 9-10 times. Renamed to `DeltaTimeScale` in the fork, the old name is an alias.
- `GameTime` +0xC: by the code it is the unscaled frame delta (+8 is the same multiplied by `DeltaTimeScale`); live, with a multiplier of 4, a server tick gives 0.1333 and 0.0333. In the fork the field is still `field_C` - to be named `RawDeltaTime` (a derived name; the game's own name was not found).
- A note on SE itself (not on a layout): `Execute` of UI commands accepts only objects, so a command with a string parameter cannot be executed from Lua; values of properties set by a binding read as nil ("complex values not supported yet"). My attempt at a native `element:ExecuteCommand()` crashed the game twice (reading the dependency property store for a binding does not give the command object) and was removed from the fork; the right way is through the reflected getters, but the `Command` property of buttons was not found that way. Deferred.

- Checks of 2026-11-13 (continued): `DeathData.InstantKill` is 1 for a party member killed by Osi.Die too; the difference the savegame model's name (AllowDownedOnDeath) suggests can only show in a party of several characters (one member's death goes to downed, not dead) - check with a recruited companion. `ScreenFadeCreationData` +0x14 was not caught live: 12 Act 1 dialogues and the Nautiloid dialogue had no fades; earlier samples came from Acts 2 and 3 (`seen.py context ecl::timeline::ScreenFadeToRequestOneFrameComponent`). `Ext.Entity.OnCreate` does fire for one-frame components, so it suits such checks.
- Systems whose size in the author's layouts differs from the game's allocation (systems survey of 2026-11-13, `research/coverage/systems/survey.csv`; the allocation is `FUN_1441beb10(heap, SIZE, ...)` in each system's factory): LockpickingSystem SE 0x88 / game 0x90 (FIXED 2026-11-13, fork 1cf92c82: the missing UniquePtr helper at +0x10, confirmed live); ls::EffectsManager 0x1E8 / 0x218; inventory::InteractionRequestSystem 0x50 / 0x90 (size from a helper, medium certainty); inventory::InteractionSystem 0x1F8 / 0x200; ls::level::LevelInstanceLoadSystem 0x70 / 0x58; ls::VisualChangedSystem 0x48 / 0x40; ecl::ConstructionSystem 0x38 / 0x30 (added by the decompilation session after its BaseSystem fix: 7 of 67 computed sizes differ). All six other systems FIXED 2026-11-13 after a live raw-memory check (fork commit after 1cf92c82: EffectsManager HashSet<Scene*> added; InteractionRequestSystem DeferredRequests restored; InteractionSystem LevelManager added; LevelInstanceLoadSystem Toolkit-only Signal removed; VisualChangedSystem FrameNo and ecl::ConstructionSystem see-through bools removed - they read past their objects).

## Coverage session: state at the pause for the GitHub migration (2026-11-13, 12:40)

State:
- Fork branch gatekit-v34 = ee9c9646 (worktree se-fork/bg3se-v34-coverage, branch coverage-v34, same commit); se-fork/builds/v34 holds its build; se_fork and se_fork_ui pass.
- Component coverage 2507 of 2647 (94.7 %, by research/coverage/coverage.json); verified live 841; 1444 generated declarations in Coverage.h, of them marked NOT VERIFIED LIVE as declared from code.
- Author definitions checked live today: fixed GameTime, DeathData names, GlobalSwitches.DeltaTimeScale, LockpickingSystem and six more systems (EffectsManager, inventory InteractionRequestSystem / InteractionSystem, LevelInstanceLoadSystem, VisualChangedSystem, ecl::ConstructionSystem); cleared spell_cast::StateComponent and LevelUpData. Open: DeathData.InstantKill (needs a party of several), ScreenFadeCreationData +0x14 (no fade caught live), the items listed above in this file.
- Built today in the bridge: GK.Gui (Client/Windows.lua, WindowReaders.lua, Popups.lua, UICatalog.lua from stand/uicatalog.py; docs/windows.md, docs/ui-catalog.md), GK.Speed (Server/Speed.lua), GK.Stage.PickLock / Disarm (lock picking checked live; disarming not seen live), stand/ui.py; the fork reads Noesis binding values (MultiBinding command parameters).
- Systems: survey in research/coverage/systems/survey.csv, plan in research/coverage/systems-plan.md (352 near-empty, 380 moderate, 170 complex; 250 with request queues).

Next steps, in order:
1. The remaining request queues of research/coverage/systems-plan.md (spell casts, statuses, drop / pick up, stacks, move-to-target, short / long rest, cooldowns, relations, passives, tadpole powers, approval): find the push in the code, check the system layout live by raw memory and the factory size, try one request live.
2. Check every remaining author system against its factory allocation size (the 7 of 67 found so far were all real errors).
3. Names pass 2 continues: about 270 gameplay layouts with field_XX names left (batches names-wiki-NN, worker brief in research/coverage/work/).
4. Translate research/coverage/design.md and research/coverage/journal.md to English when next touched (project rule of 2026-11-13).
5. Deferred by the user: an in-game Weave panel for speed by situation and other commands (research/coverage/design.md, "Deferred").
