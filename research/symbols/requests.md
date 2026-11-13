# Engine functions asked for by the test stand

Answers from the symbol database (`symbols.sqlite`, Toolkit 4.1.1.6931813, game 4.1.1.7631656,
built 2026-11-12). Nothing here was tried in the running game.

How to read the tables:

- **Toolkit** columns are addresses in the editor's DLLs. They are for reading the code, not for
  calling: the game is a different build.
- **Game** columns are offsets from the image base of that executable. *How it was found* says
  how much to trust it:
  - `table slot` — the function sits in the matching slot of the class's virtual table, and the
    table was recognised by 16–17 agreeing slots. Solid.
  - `call order` — I compared the body of a function that is identified in both builds
    (`App::OnInputDeviceUsed`) and paired its calls by position and by the object they are called
    on. Plausible, one witness. Look at the function before calling it.
  - `global, SE` — located by Script Extender's own byte pattern.
- Every game address changes with the next patch. `python symbols.py signature <exe>+0x<rva>`
  prints a byte pattern for it (needs capstone; use the GateKit environment's Python).

## 1. Switching between keyboard and controller mode

### What the code does (Toolkit, read from the function bodies)

```
App::OnInputDeviceUsed(player, device, bool)             the engine's own reaction to "this device was just used"
  ├─ ecl::EocClient::SetKeyboardMode(bool)               when a keyboard/mouse was used
  └─ ecl::EocClient::SetControllerMode(EInputDeviceID)   when a controller was used

ecl::EocClient::SetControllerMode(device):
    if (BaseApp::GetAppInstance()->AreInputTypeModeChangesBlocked()) return;
    ls::InputManager::GetInstance()->SetControllerMode(device, true);
    <member at this+0x40>.something(device);             (not exported; tells the client side)

ls::InputManager::SetControllerMode(device, bool):
    <static bool> = flag;                                 the "controller mode" switch itself
    if (device == -1) device = 3;
    SetPlayerDevice(player 0, device);
    ... reassigns devices of players 1..3 ...
    fires the device listeners                            -> OnControllerModeChanged on every listener
```

So the entry point that does everything, including notifying the interface, is
`ecl::EocClient::SetControllerMode` / `SetKeyboardMode`. Calling
`ls::InputManager::SetControllerMode` directly skips the client-side part.
`BaseApp::AreInputTypeModeChangesBlocked()` can veto the switch: if a call does nothing, check
that first.

There are also two console commands, `ecl::EocClient::CommandSetInputMode` (arguments `auto`,
`keyboard`, `controller`; it "sets and locks" the mode) and `ecl::EocClient::CommandInputController`.
Their message texts are not in the game executable, so the commands were most likely compiled
out of the game; they are useful only as a reading of how Larian's own code switches the mode.

### Functions

| Function | Toolkit | `bg3_dx11.exe` | `bg3.exe` | How it was found |
|---|---|---|---|---|
| `public: virtual void App::OnInputDeviceUsed(enum ls::EInputPlayerIndex, enum ls::EInputDeviceID, bool)` | `EoC.dll+0x19B1E0`, slot 5 of `App::vftable{for ls::InputDeviceListener}` | `+0x5E3C60` | `+0x5E4BD0` | unique text constant, sizes agree |
| `public: void ecl::EocClient::SetControllerMode(enum ls::EInputDeviceID)` | `EoCClient.dll+0x529E10` | `+0x5EB2B0` | `+0x5EC1C0` | call order |
| `public: void ecl::EocClient::SetKeyboardMode(bool)` | `EoCClient.dll+0x52A0D0` | `+0x5EB490` | `+0x5EC3A0` | call order |
| `public: bool ls::InputManager::HasConnectedController(void) const` | `CoreLib.dll` | `+0x40F47F0` | `+0x412ACC0` | call order |
| `public: void ls::InputManager::SetControllerMode(enum ls::EInputDeviceID, bool)` | `CoreLib.dll+0xD4C30` | not found (probably inlined into `EocClient::SetControllerMode`) | not found | |
| `public: void ls::InputManager::SetKeyboardMode(bool)` | `CoreLib.dll+0xD4D20` | not found | not found | |
| `public: void ls::InputManager::SetPlayerDevice(enum ls::EInputPlayerIndex, enum ls::EInputDeviceID)` | `CoreLib.dll+0xD4E00` | not found | not found | |
| `public: bool BaseApp::AreInputTypeModeChangesBlocked(void) const` | `GameEngine.dll+0xA498D0` | not found | not found | |
| `public: void ecl::InputController::SetControllerMode(bool)` | `EoCClient.dll+0xA7DB20` (928 bytes; per-character controller) | not found | not found | |
| `public: bool ecl::InputController::GetControllerMode(void) const` | `EoCClient.dll+0x55B060` — reads bit 0 of the byte at `this+0x48` | | | |
| `public: void gui::ViewModelProvider::SetControllerType(enum ls::EControllerType)` | `NoesisGameUI.dll+0x2EF9E0` | not found | not found | |

Objects to call them on:

| Global | `bg3_dx11.exe` | `bg3.exe` | How it was found |
|---|---|---|---|
| `ecl::EocClient*` (pointer to the client object; `this` for the two `EocClient` methods) | `+0x5FDA240` | `+0x6263C08` | global, SE (`ecl__EoCClient`) |
| `ls::InputManager*` | `+0x604B1A8` | `+0x62D49E0` | global, SE (`ls__gInputManager`) |

Later confirmed in the decompiled `bg3.exe`: the pseudo-code of `App::OnInputDeviceUsed`
(`bg3.exe+0x5E4BD0`) calls `FUN_1405ec1c0(ecl__EoCClient, param_2 & 0xffff)` — the client object
and the 16-bit device id, the shape of `SetControllerMode(EInputDeviceID)` — and
`FUN_1405ec3a0(ecl__EoCClient)` on the other branch, and tests `FUN_14412acc0(ls__gInputManager)`
before them. That is a second witness for the three `bg3.exe` addresses in the table.
(`python symbols\decomp\decomp.py code bg3.exe+0x5E4BD0` prints it.)

The pseudo-code of `bg3.exe+0x5EC1C0` itself settles it. It is `EocClient::SetControllerMode`
with `InputManager::SetControllerMode` inlined into it, line for line what the Toolkit code does:

```c
void FUN_1405ec1c0(longlong param_1, short param_2)        // this, device id
  if (*(char *)(DAT_1462d0718 + 0x231) == 0 && *(char *)(DAT_1462d0718 + 0x232) == 0) {   // mode changes not blocked
    sVar12 = 3;  if (param_2 != -1) sVar12 = param_2;     // -1 means "first controller"
    DAT_1463d9438 = 1;                                     // the controller-mode switch
    FUN_14412a800(ls__gInputManager, 0, sVar12);           // InputManager::SetPlayerDevice(player 0, device)
    ...
```

So, in `bg3.exe` 4.1.1.7631656: the controller-mode flag is the byte at `+0x63D9438`,
`ls::InputManager::SetPlayerDevice` is `+0x412A800`, and the "mode changes blocked" test reads
the bytes at `+0x231` and `+0x232` of the object whose pointer is at `+0x62D0718` (the
application object, by the Toolkit code `BaseApp::AreInputTypeModeChangesBlocked`). Reading the
flag is harmless; writing it alone skips the device reassignment and the listener notification
that the function does after it.

In `bg3_dx11.exe` the two calls inside `App::OnInputDeviceUsed` read exactly this:
`mov rcx, [ecl__EoCClient]; call +0x5EB490` and `mov rcx, [ecl__EoCClient]; call +0x5EB2B0`.

`EInputDeviceID` is 16 bits wide. In `InputManager::SetControllerMode`, `-1` is replaced by `3`
and the loop over other devices runs from `3` to `10`, so controller device ids start at 3;
keyboard and mouse are below that. Script Extender's source agrees
(`BG3Extender/GameDefinitions/Input.h:15`, `EInputDeviceId : int16`): `C = -2`, `Unknown = -1`,
`Key = 0`, `Mouse = 1`, `Controller1 = 3` ... `Controller8 = 10`, `Touchbar = 11`.

### Without native calls

Script Extender's `InputManager` struct has, next to the `InputInjects` you already tried, a
second queue: `DeviceEventInjects`, an array of `InjectDeviceEvent { uint32 EventId; InputDeviceId DeviceId; }`
(`BG3Extender/GameDefinitions/Input.h:99` and `:254`). A device event is what reaches
`App::OnInputDeviceUsed` and its siblings (`OnInputDeviceConnected`, `OnControllerDeviceLinked`),
so injecting one may switch the mode where injecting a button press did not. The event ids are
not named in the source; I have not tried it.

## 2. Skipping a cutscene, jumping to the next dialog node

### What the code does (Toolkit)

```
ecl::TimelineSystem::SkipWithinSequence(LSDialogs::DialogInstance const& dialog) -> bool
    can = CanSkipWithinSequence(dialog, out float time, out uint64 phase);
    timeline = GetDialogueTimeline(dialog.id);
    if (timeline && can)
        RequestPlaybackTime(timeline->guid /* at +0xA8 */, time, phase, trigger = 0x700);
    return can;
```

This is the engine's own "skip to the next point" (what the skip key does inside a dialog). It
does not end the timeline; it asks the system to move playback to the time and phase
`CanSkipWithinSequence` computed. `RequestPlaybackTime` is the general "go to this time" request
and takes the timeline's guid, so it can be used for any jump.

The low-level timeline object (`aspk::Timeline`, base of `ls::Timeline`) is simple:

| Method | Body | Meaning |
|---|---|---|
| `aspk::Timeline::JumpToTime(float)` | `[this+0x38] = t; byte [this+0x20] = 2` | sets `JumpToTime` and puts the state byte to 2: the jump happens on the next update |
| `aspk::Timeline::JumpToPhase(unsigned __int64 phase, bool)` | 78 bytes; `[this+0x3C] = phase`, clamps to the phase count, then walks the phases | called by `ls::EffectComponent::SetPhase` |
| `aspk::Timeline::SetPlayingSpeed(float)` | `[this+0x28] = speed` | called by the `TLPlayRate` track |
| `ls::Timeline::SetSkippingMode(enum ls::ESkippingMode)` | `[this+0x490] = mode` | |
| `ls::Timeline::GetSkippingMode()` | `return [this+0x490]` | |

Offsets are the Toolkit's. Script Extender exposes the same fields on `aspk::Timeline` in Lua
(`JumpToTime`, `JumpToPhase`, `PlayingSpeed`, `State`, `IsPaused`, `PlayToEnd`), which suggests a
route without native code: write `JumpToTime` and set `State` to 2, exactly what `JumpToTime()`
does, or raise `PlayingSpeed`. Whether those fields are writable from Lua in v33 and whether the
server follows a client-side jump has to be tried; for a dialog timeline the server
(`esv::TimelineSystem`) owns the flow, and `RequestFastForwardIfNeeded` /
`ProcessProtocolRequestFastForward` are how the two sides agree on a fast-forward.

### Functions

| Function | Toolkit | `bg3_dx11.exe` | `bg3.exe` | How it was found |
|---|---|---|---|---|
| `public: bool ecl::TimelineSystem::SkipWithinSequence(class LSDialogs::DialogInstance const &)` | `EoCClient.dll+0xC8E910` | not found | not found | |
| `public: bool ecl::TimelineSystem::CanSkipWithinSequence(class LSDialogs::DialogInstance const &, float &, unsigned __int64 &)` | `EoCClient.dll+0xC7A2B0` | not found | not found | |
| `public: void ecl::TimelineSystem::RequestPlaybackTime(class ls::Guid const &, float, unsigned __int64, enum ls::timeline::ETimelineUpdateTrigger)` | `EoCClient.dll+0xC8DAC0` | not found | not found | |
| `private: void ecl::TimelineSystem::ProcessProtocolRequestFastForward(class ls::Guid const &, bool)` | `EoCClient.dll+0xC84A50` | not found | not found | |
| `private: void esv::TimelineSystem::RequestFastForwardIfNeeded(...)` | `EoCServer.dll+0xE85A60` | not found | not found | |
| `private: void esv::TimelineSystem::EndTimeline(struct ls::ID<struct ecs::EntityHandleTraits> const &)` | `EoCServer.dll+0xE77750` | not found | not found | |
| `public: void ecl::GameControl::SkipMovie(void)` / `SkipMovie(class ls::FixedString const &)` | `EoCClient.dll+0xAA4EB0` / `+0xAA72D0` (video files, not timelines) | not found | not found | |
| `public: void LSDialogs::DialogManager::SelectAnswer(struct ls::ID<struct ecs::EntityHandleTraits> const &, unsigned char, unsigned int, class ls::FixedString const &)` | `LSDialogs.dll+0x3B250` | not found | not found | |
| `aspk::Timeline::JumpToTime`, `JumpToPhase`, `SetPlayingSpeed`; `ls::Timeline::SetSkippingMode` | `AllSparkCore.dll+0xC260`, `+0xC0C0`, `+0xCDB0`; `GameEngine.dll+0x402980` | not found as functions (a few instructions each: inlined) | | use the fields |

Not finding `ecl::TimelineSystem`'s own methods in the game is the general state of things, not
something particular to these: its virtual table was not recognised and only its constructor is
located (`bg3_dx11.exe+0x1D6E730`, `bg3.exe+0x1D6FA90`). Candidates can be narrowed with
`python symbols.py users ecl::TimelineSystem` (functions that use the system's index) and, once
the game is decompiled, by searching the pseudo-code for the constant `0x700` passed as the fifth
argument next to a call that takes `timeline + 0xA8`.

### Virtual methods of the timeline object — found in the game

Both tables were recognised with every exported slot agreeing, so these can be called through
the object's own table (slot × 8 from the pointer at offset 0 of the object) with no address at
all, or directly:

| Slot | Method | `ls::Timeline` in `bg3_dx11.exe` / `bg3.exe` | `aspk::Timeline` in `bg3_dx11.exe` / `bg3.exe` |
|---|---|---|---|
| 1 | `virtual void Play(void)` | `+0x3CBA0B0` / `+0x3CBA7B0` | `+0x3F98460` / `+0x3F9B770` |
| 2 | `virtual bool IsStopped(void) const` | `+0x579DA0` / `+0x57AA60` | `+0x3F984F0` / `+0x3F9B800` |
| 3 | `virtual void Clear(void)` | `+0x3CBA210` / `+0x3CBA910` | `+0x3F97FC0` / `+0x3F9B2D0` |
| 4 | `virtual void Reset(bool)` | `+0x3CBA0C0` / `+0x3CBA7C0` | `+0x3F98580` / `+0x3F9B890` |
| 5 | `virtual void PostUpdate(float)` | `+0x3F99360` / `+0x3F9C670` | same |
| 8 | `virtual void Stop(void)` | `+0x3CBA1C0` / `+0x3CBA8C0` | `+0x3F98390` / `+0x3F9B6A0` |
| 12 | `virtual void OnHoldPhaseCompletion(void)` | `+0x3CBA890` / `+0x3CBAF90` | |
| 14 | `virtual void UpdateComponents(...)` | `+0x3CBC210` / `+0x3CBC910` | `+0x3F98B30` / `+0x3F9BE40` |

Virtual tables: `ls::Timeline` at `bg3_dx11.exe+0x58B4EF8` / `bg3.exe+0x58C9AE8`,
`aspk::Timeline` at `bg3_dx11.exe+0x58DEA90` / `bg3.exe+0x5B73850`. If the object the game plays
is of a class derived from `ls::Timeline`, that class's own table is not recognised here, but the
slots it inherits without overriding hold the `ls::Timeline` functions above.

`Stop()` on the client's timeline object ends playback locally. For a dialog the server still
waits for the node to finish, so `Stop` alone is likely to leave the dialog hanging: the request
path (`RequestPlaybackTime`, or fast-forward through the server) is the one the engine uses.

## Searching the database yourself

```
python <old repo>\symbols\symbols.py find SetControllerMode          names in the Toolkit
python <old repo>\symbols\symbols.py class ecl::TimelineSystem       every exported member
python <old repo>\symbols\symbols.py game Timeline --dll bg3_dx11    what was found in the game
python <old repo>\symbols\symbols.py vtable ls::Timeline             slot order
python <old repo>\symbols\symbols.py fields aspk::Timeline           fields with offsets
python <old repo>\symbols\symbols.py callers "EocClient::SetControllerMode"
python <old repo>\kit\gatekit.py hook "Timeline::Stop" --json         one answer, machine-readable
python <old repo>\kit\gatekit.py search <anything>
```

`symbols\README.md` and `kit\GUIDE.md` describe the rest; `kit\mcp_server.py` offers the same as
MCP tools (`bg3_search`, `bg3_about`, `bg3_hook`, `bg3_sql`, ...).

What is ready: `symbols\` — Toolkit exports, virtual tables, fields, Script Extender layouts and
enumerations, ECS types, 24,057 Toolkit functions located in `bg3.exe` (3,732 with a declaration);
`index\` — every game file, timelines with tracks, dialogs with flow and flags, templates, placed
objects, stats, Osiris, localization. Pseudo-code of `bg3.exe` is being produced and is not
loaded yet.
