# Offer: map `GlobalSwitches::SoundSettings`

Branch `offer/sound-settings` in the fork's clone, cut from the upstream commit the fork is based
on (`base/v34-imgui_v1_93-2ea16d51`, i.e. `Norbyte/bg3se` `imgui_v1_93` at `2ea16d51`), carrying
this one change and nothing of GateKit's. **Not pushed. No pull request.**

It is finished to a sendable standard: the code, the description and the commit message are what
would go. When it goes is the README's business, not this file's — after GateKit is published,
on the owner's word and from his account.

Below is the description as it would be sent: a title, a body, and nothing about where it came
from. Everything it claims was either read out of the game's own code or seen in a running game,
and the last section says what it does not claim.

---

## Title

Map `GlobalSwitches::SoundSettings`

## Body

`SoundSettings` is declared in `GameDefinitions/Misc.h` but is missing from the property map, so
`Ext.Utils.GetGlobalSwitches().SoundSettings` fails with *property does not exist*. That leaves
the player's own sound settings unreadable from Lua: a mod can set a Wwise RTPC, but it cannot
find out where the player put the sliders. This adds the map entries. The layout is unchanged
except for `SoundSetting[12]` becoming `std::array<SoundSetting, 12>`, which the array proxy
needs and which has the same layout.

### What the structure is

`SoundSetting` is 0x60 bytes and there are twelve of them, at `EoCGlobalSwitches+0x1E0` in
`bg3.exe` / `bg3_dx11.exe` 4.1.1.7631656, so the array ends at 0x660. The loop at
`bg3_dx11.exe+0x5CDF10` walks them outright:

```
mov  ebx, 0x1E0
mov  rdx, [rip+...]      ; the EoCGlobalSwitches*
add  rdx, rbx
mov  eax, [rdx]          ; ConfigValue[0]
mov  [rdx+4], eax        ; -> ConfigValue[1]
call qword [rdx+0x58]    ; UpdateProc
add  rbx, 0x60
cmp  rbx, 0x660
jl   ...
```

In the shipped Toolkit the same function is exported by name: `App::ApplySoundVolumeSettings`
(`EoC.dll+0x182280`), with the array at `+0x250` because that build has extra fields in front
of it.

The three floats of `ConfigValue` are not interchangeable:

- **`ConfigValue[1]` is the value in effect.** `UpdateProc` — one function, shared by all twelve
  (`EoCShared.dll+0xD77700`) — reads `[this+4]` and writes it to every `FixedString` in `RTPCs`
  through the Wwise `SetRTPCValue` call. Nothing else in these structures reaches Wwise.
- **`ConfigValue[0]` is the value configured for `ConfigKey`.** The settings reader
  (a virtual, `bg3_dx11.exe+0x5D0360`) looks each `ConfigKey` up in the loaded config map and
  writes what it finds into `[0]` *and* `[1]`, then calls `UpdateProc`.
  `App::ApplySoundVolumeSettings`, above, copies `[0]` to `[1]`.
- **`ConfigValue[2]` is the built-in default.** It is what the constructor seeded and what a
  reset goes back to: `eoc::EoCGlobalSwitches::ResetSoundSettings` (`EoCShared.dll+0xD77300`,
  called from `EoCGlobalSwitches::Reset`) copies `[2]` to `[1]` and calls `UpdateProc`. Reading
  the settings leaves it alone, which is how it can be told apart from `[0]`.

All three start equal: the constructor (`EoCShared.dll+0xD773C0`) takes one float and stores it
three times (`movss [rcx],xmm1` / `[rcx+4],xmm1` / `[rcx+8],xmm1`). Eleven of the twelve are
constructed with 100.0, the twelfth from a global.

The twelve keys, in the array's order, are `MasterVolume`, `FxVolume`, `AmbientVolume`,
`UIVolume`, `MusicVolume`, `MusicFightVolume`, `CutsceneVolume`, `VoiceVolume`,
`VoiceVolumeDialog`, `VoiceVolumeNarrator`, `VoiceVolumeOverhead`, `RumbleVolume`; each has one
RTPC (`RTPC_Volume_MAIN`, `RTPC_Volume_SFX`, … and `RTPC_Rumble` for the last). The options
screen shows seven of them.

`field_44` is the name the options screen draws for the row, so it is renamed `DisplayName`.

### What it was checked against

In a running game (4.1.1.7631656, DX11), read with `Ext.Debug.ReadMemory` from the
`EoCGlobalSwitches*` the apply loop above uses: twelve entries at `+0x1E0`, stride `0x60`, every
`UpdateProc` the same address, every `ConfigKey` and `RTPCs` where this layout says they are.

With the map entries in place, `Ext.Utils.GetGlobalSwitches().SoundSettings` returns the twelve
settings, and they tell the three floats apart. With the master volume at 37 and the music at 42
in the profile and the other ten untouched, the game read back

```
MasterVolume          37   37  100     RTPC_Volume_MAIN       "<the options screen's own label>"
MusicVolume           42   42  100     RTPC_Volume_Music      "<the options screen's own label>"
...every other        100  100  100
```

— the configured value in `[0]` and `[1]`, the default left standing in `[2]`. Every
`DisplayName` resolves through `Ext.Loca.GetTranslatedString` to that row's label in the options
screen, in the game's own language.

`[0]` and `[1]` also come apart on their own, which is the clearest thing I saw. With
`MuteSoundWhenNotFocused` on and the window behind another one, the game silences itself, and
it does it by writing `[1]`:

```
MasterVolume     ConfigValue  100  0  100     RTPC_Volume_MAIN       reads 0
CutsceneVolume   ConfigValue  100  0  100     RTPC_Volume_Cinematics reads 0
every other      ConfigValue  100 100 100     its RTPC               reads 100
```

The player's value stays in `[0]`, the mixer and `[1]` go to 0 together, and the default is
untouched. I did not watch the window come forward again, so what restores `[1]` afterwards is
inference rather than observation; `App::ApplySoundVolumeSettings` is the obvious candidate,
since copying `[0]` into `[1]` and calling `UpdateProc` is all it does.

### What I could not determine

- **What the options screen writes when the player moves a slider.** I could not drive a slider
  under observation, so what I saw is the settings being read, not written. Two things I noticed
  and cannot account for: the screen does not draw these floats (with 11/22/33 written into the
  master entry it still showed 100 on every row, so it reads the settings store), and the
  settings writer — the virtual next to the reader, `bg3_dx11.exe+0x5D3EE0` — serialises the
  pair `(ConfigKey, ConfigValue[2])`, that is, the default rather than the configured value.
  Every other field in that pair of functions is read into and written from the same offset
  (`+0x660`, `+0x13C0`); the sound settings are the one place where they disagree. Nothing in
  this change depends on it: the three floats are mapped as the array they are.
- **`field_40`**, one byte, left as it was. It is 1 on `VoiceVolumeDialog` and 0 on the other
  eleven in the game I read; I have no account of what it means.
- `UpdateProc` is left out of the map on purpose: it is a bare function pointer into the
  executable and nothing good comes of handing it to Lua.

### One question

Much of the work behind this was done by AI agents, directed by a person, with every claim
checked in a running game before it was written down. You should know that rather than find it
out later.

There is more of this kind of work — component layouts and field names, each checked the same
way. If any of it is useful to you, tell us the shape you would like it in: how large a change
should be, how comments and names should read, what evidence you want with it. If you would
rather not have it, say so and we will keep it to our own build; no explanation needed.

---

## The commit

```
Map GlobalSwitches::SoundSettings

SoundSettings is declared but absent from the property map, so
Ext.Utils.GetGlobalSwitches().SoundSettings fails with "property does not
exist" and a mod cannot read the player's sound settings at all.

Map SoundSetting and the array. SoundSetting[12] becomes
std::array<SoundSetting, 12> for the array proxy; the layout is the same.
field_44 is the row's name in the options screen, so it is named DisplayName.
LuaStructIDs.h needs GameDefinitions/Misc.h because a nested struct cannot be
forward-declared.

The comment on ConfigValue says which of the three floats is which: the
configured value, the value in effect and the default. Checked in a running
game with two of the twelve set away from their defaults.
```

Three files: `GameDefinitions/Misc.h`, `GameDefinitions/PropertyMaps/Stats.inl`,
`Lua/Shared/Proxies/LuaStructIDs.h`, +15 -3. The generated files under
`GameDefinitions/Generated/` are not committed upstream either, so they are not in the diff;
`generate-property-maps.bat` regenerates them.

Authored by `Gwynerva`, with the usual `Co-Authored-By: GateKit Thrall` trailer, as the README
of this folder asks: the person opens it, the commit credits both. Nothing in the code itself
names GateKit.

## Which branch it goes to, and whether it still applies

**Target `main`.** Checked on 2026-11-13: upstream's `main` is at `43ee0399` ("Add native
exports"), and this change applies to it cleanly - the three files it touches are untouched
there since the base. `imgui_v1_93`, the branch the fork is cut from, is still at `2ea16d51` and
would also take it, but it is a feature branch and the mainline is where a property map belongs.

Re-check this before sending, with the patch rather than by eye: upstream moves, and a first
contact that arrives needing a rebase makes more work for the person being asked a favour.

## Where it is

- `offer/sound-settings`, one commit on `2ea16d51`, in the worktree `se-fork/bg3se-offer-sound`.
  Not pushed. A pull request has to come from a fork on GitHub, so the fork repository has to
  exist first — pushing it with this one upstream-shaped branch would be enough, and the README
  of this folder says when that happens: after GateKit is published, on the owner's word and
  from his account.
- The same change is in the fork's own branch, `gatekit-v34` → `sound-v34` at `28aef48a`, built
  and run; `se-fork/bg3se-v34-sound/x64/Game Release/BG3ScriptExtender.dll` is that build. It was
  **not** copied over `se-fork/builds/v34`.
- GateKit's side of it — `GK.Sound.Settings`, the contract and the type stubs — is on the
  `sound-settings` branch of the toolkit.
