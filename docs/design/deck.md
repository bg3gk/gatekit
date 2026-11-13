# The deck: every game in one window

**Status: built.** The window, the tiles, the commands and both backings — `adopt` and `mirror` —
exist; `src/GateKit.Deck/README.md` is how. What is still only design is marked where it appears.

Running several games at once works, and is one of the reasons GateKit exists — an agent plays
in one, a scenario runs in another, you watch a third. What does not work is *living* with them:
each new game throws its own window over everything you were doing and steals the keyboard,
Script Extender adds a console window per game, the windows look alike so the wrong one gets
closed, and Windows asks about the firewall every time.

The deck is one window that owns them all.

```
┌─ GateKit ─────────────────────────────────────────────────┐
│ ┌─ agent · Nautiloid run ─────┐ ┌─ gk · scratch ────────┐ │
│ │                             │ │                       │ │
│ │   (the game, embedded)      │ │  (restarting…)        │ │
│ │                             │ │                       │ │
│ └─────────────────────────────┘ └───────────────────────┘ │
│ ┌─ coverage · tour 12 ────────┐ ┌───────────────────────┐ │
│ │                             │ │     (empty)           │ │
│ └─────────────────────────────┘ └───────────────────────┘ │
└───────────────────────────────────────────────────────────┘
```

## What it must do

**Take the window, not the focus.** A game started by the stand never comes to the front and
never takes the keyboard. It appears in its tile in the deck, and whatever you were typing in
keeps the caret.

**Name every copy.** A tile carries the instance name, a label you can set (`--label "Nautiloid
run"`), and the game's process id. You should never have to guess which window is which, and
`gatekit down` on the wrong instance should be impossible to do by accident.

**Tile by itself.** With nothing maximized the tiles fill a grid: one fills the deck, two sit
side by side, three or four make two rows of two, then three columns, and so on. Empty
quadrants are fine — a tile keeps its place when its game exits, so the layout does not jump
around while you are watching.

**Hold a tile open.** `gatekit window maximize agent` gives that copy the whole deck and keeps
it there. If the game restarts — a crash, a `reset`, a new `up` — the tile stays the same size
and shows that it is waiting, then takes the new window when it appears. A run you are watching
should not throw your layout away because the game blinked.

**Change nothing about reading the game.** Logs, screenshots and the bridge must work exactly as
they do now for a copy that is behind another, in a tile that is not visible, or minimized —
nothing may start depending on a window being on top. This is the rule the whole toolkit already
holds to, and the deck must not quietly break it.

**Resize with the deck.** Drag the deck's edge and the tiles follow; the game inside a tile
re-lays itself out rather than being cropped or stretched.

**No Script Extender console windows.** One console window per game is noise. The extender's
output goes to its log files, and `gatekit logs` reads those; nothing on screen.

## How it is built

**Embedding.** On Windows a window can be adopted: `SetParent` into the deck, clear
`WS_POPUP`, set `WS_CHILD`, then position it in its tile. The game keeps rendering; it does not
know it moved. The pieces to get right:

- **Find the right window.** The stand already finds the game window by process and class
  (`SDL_app`), which also keeps it from grabbing the extender's console by mistake.
- **Do not activate.** Start the process without stealing the foreground, and use
  `SWP_NOACTIVATE` everywhere. The game is told its window moved, not that it was clicked.
- **Input.** A child window still receives posted keys, which is how `gatekit key` works today.
  Clicking a tile should give that game the keyboard, and only then.
- **Let it go cleanly.** On `down`, or if the deck closes first, the window is released back to
  the desktop rather than destroyed under the game's feet.

**When embedding fails** — a driver, a full-screen mode, a future patch — the deck must degrade,
not break: the copy runs in its own window as it does today, the tile shows where it went, and
everything else still works. This is a convenience, never a dependency.

**The deck is a host program**, `gatekit deck`, not part of the stand: the stand keeps starting
games whether a deck is running or not. They find each other through the state folder, so a deck
started later adopts the games already running.

**The API** is the point, not the window: `gatekit window list|maximize|grid|label|release`, and
the same through MCP, so an agent can say "keep my run visible" and a person can rearrange
without the agent noticing.

## The firewall prompt

Windows asks for permission the first time the game listens on a socket, for every copy. It is
not something a program may answer for you, and GateKit will not touch the firewall: changing
security settings is yours to do. What GateKit can do is notice the prompt is coming and print
the one command that settles it, to run once in an elevated prompt:

```powershell
New-NetFirewallRule -DisplayName "Baldur's Gate 3 (GateKit instances)" -Direction Inbound `
  -Program "<the game's bin>\bg3_dx11.exe" -Action Allow -Profile Any
```

A rule for the installed executable covers every instance, because an instance's `bin` is hard
links to the same file — which `gatekit doctor` should check and say.

## What has to be built

| | |
|---|---|
| host | `gatekit deck`: the window, tiles, grid, adoption, release; `window` commands; the MCP tools |
| platform | adopt/release/position a window, start without activating (Windows now; X11 later, never Wayland) |
| mirror | **built**: a tile backed by the game's own frames, shared on the graphics card, so the deck works where adopt cannot and a mirrored copy's window need not be seen at all. Input still goes through the game's own code (`GK.Key`) or posted messages |
| stand | start a game into a deck when one is running; keep the tile when a game restarts; stop creating the extender console |
| doctor | whether a deck is running, whether the firewall rule exists |
| docs | the deck in `docs/concepts.md`, the commands in `docs/contracts/cli.md` |

## Two ways to show a game, and only one of them is Windows-only

**Adopt** — take the game's own window and put it in a tile. The game renders as it always does,
at full frame rate, and a click gives it the keyboard directly. This is what the section above
describes, and it is as portable as window embedding is: Windows now, X11 later, never Wayland.

**Mirror** — do not touch the window at all. Ask the game for its frame and draw it in the tile;
send a click or a key back through the game's own input. Everything in that sentence is
something GateKit is building anyway, for screenshots and keys that work without a window
(`research/TODO.md`), and none of it is operating-system specific: it works under Wayland, under
Proton, over a network, and for a game whose window is minimized.

### Mirror is not a stream of screenshots

The first idea for mirror was to take a picture over and over, and that would indeed be
unbearable: a capture costs 0.33 s through the window and 1.5 s through the game, almost all of
it moving pixels from the graphics card into memory and encoding a PNG. Nobody wants a tile at
one frame a second.

The way this is actually done — by every screen recorder and by Windows' own window capture — is
never to bring the pixels back at all. The graphics card can hand a texture to another program
directly: our Script Extender build already sits in the game's present call, where the finished
frame is; instead of copying it down into memory, it copies it on the card into a **shared
texture** and passes the deck a handle. The deck draws that texture. Nothing crosses into
system memory, nothing is encoded, and the cost is a GPU-to-GPU copy of one frame — which is
what lets recorders do this at sixty frames a second while a game runs.

That turns mirror from the slow fallback into the better mode, and it brings something adopt
cannot do:

**the game's window never has to be seen.** It still needs one — a swap chain is attached to a
window, and taking that away means fighting the engine rather than reading it — but it can be
created hidden or placed off the screen. What you see is the deck, drawing the frames. No window
to steal focus, nothing to flash up when a game starts, no row of identical Baldur's Gate icons
filling the taskbar, and tiles that can be scaled and arranged freely because they are textures
rather than someone else'''s window.

Two things had to be checked before believing it, and both now have answers rather than hopes.

- **Does the game keep rendering when its window is hidden?** Engines often throttle or stop when
  minimized. BG3 keeps running unfocused — the toolkit relies on that already — but hidden is
  not the same as unfocused, and the macOS port of the extender needed a flag for exactly this.

  **It does.** A copy whose window was hidden outright (`ShowWindow(SW_HIDE)`) kept presenting at
  29.67 frames a second against 29.83 with the same window shown again, and went on copying every
  one of them into the shared texture. Nothing had to be asked of the engine.

  The deck still puts a mirrored copy's window **off every screen** rather than hiding it, and
  that is the default (`GATEKIT_DECK_CONCEAL=offscreen`): moved past the edge of the desktop and
  made a tool window, it is on no screen and in no taskbar, while GateKit's own window fallbacks —
  `shot` without the bridge, `key` without the bridge, `window list` — keep finding it, because
  they look for a *visible* window. `hidden` is there for whoever wants it and is proven to work;
  it simply costs those fallbacks.

  Measured again once the background throttle could be lifted, because the first measurement was
  of a throttled game and "it keeps drawing" is a weaker claim at thirty a second than at a
  hundred and thirty: **a game whose window is hidden outright draws at 131-135 frames a second**
  with the cap lifted, its tile holding a steady 60 and sitting 4.5 ms behind it, and a hidden
  copy capped at ten draws exactly ten. Hiding the window costs the game nothing at all. On those
  same two hidden copies `exec` answered, `key` reached the game (the Escape menu opened in the
  picture) and `shot` wrote a PNG of the game taken from inside it - the rule the whole toolkit
  rests on, checked where it is easiest to break.

- **How much does the extra copy cost the game?** Measured twice, and the second time changes how
  the first should be read. Two copies mirrored side by side, each at 1920×1080 on an HDR10 swap
  chain, **while the engine's background throttle was still in force** — so both games sat at a
  flat thirty a second and the two frame rates below are a ceiling touched by either way:

  | | |
  |---|---|
  | the game's frame rate, mirroring **off** / **on** | 30.04 / 30.06 a second |
  | the copy, on the rendering thread | 0.41–0.53 ms, **1.3–1.6 % of a 33 ms frame** |
  | what a tile reaches | 29.9 against the game's 29.9; 30.0 against 30.0 filling the whole deck |
  | from the frame being presented to it being on the screen | 8.0–8.5 ms |
  | frames the game skipped because the deck held the texture | 7 in some fifteen thousand |

  Then the throttle came off (the section below), and the same copy was measured again with
  nothing holding it back, a second game running beside it and a deck drawing both:

  | | |
  |---|---|
  | the game, mirroring, uncapped | **144.2 frames a second** |
  | what the tile reached | 60.1 a second — the tile's own ceiling, not the game's |
  | from the frame being presented to it being on the screen | **4.2 ms** |
  | the copy, on the rendering thread | 0.70 ms |
  | frames skipped because the deck held the texture | 35 |

  The copy's share of a frame is not a constant: 0.5 ms is under 2 % of a throttled 33 ms frame
  and nearly 10 % of an uncapped 7 ms one. So it was measured where it counts — mirroring switched
  on and off under the same game running flat out, eight alternating fifteen-second samples, one
  tile held open over the deck and a second game beside it at ten a second:

  | mirroring | frames a second |
  |---|---|
  | off | 135.8, 152.7, 156.4, 152.1 |
  | on | 150.9, 151.4, 147.5, 150.0 |

  **149.2 against 149.9 a second over all eight** — indistinguishable, with mirroring the
  nominally faster. Dropping the first pair, where the game had just been given the whole deck and
  was still settling, leaves 153.7 against 149.6: **2.7 % of the frame time**. Under the 5 % bar
  either way, at 150 frames a second.

  That also says what the third row of each table is and is not. The rendering thread's time
  inside the copy is an upper bound on what mirroring costs, not a measurement of it: the card
  does the copy while the frame is on its way out, so most of that 0.5-0.7 ms never lands on the
  game's own frame time.

The readback path we have now stays as the fallback for anything that cannot share a texture,
and as the way `shot` writes a PNG to a file. On Vulkan, and so on Linux, the same idea exists
with external memory, but our build has no Vulkan capture at all yet, so Windows comes first.

**The bar to clear before mirror ships** was a tile that keeps up with the game at a readable
frame rate while costing it under 5% of its frame time. **Both halves are cleared**: a tile holds
sixty a second against a game running at 144, four milliseconds behind it, and mirroring costs that
game 2.7 % of its frame time at 150 a second — or nothing measurable at all, depending on whether
the settling sample is counted. Mirror is built and proven, and still not the default, for the
reasons below rather than for its cost.

**Mirror is built and is not the default.** `gatekit window mirror <instance>` and
`window adopt <instance>` switch one tile while the deck runs, and `GATEKIT_DECK_BACKING` is the
default for new tiles — **`adopt`**. Not because mirror is slower: because adopt is the one that
has been proven on real games for months, needs nothing of the game, and is better for playing in
a tile yourself, while mirror asks the game for something only GateKit's Script Extender build on
the DirectX 11 executable can give. A copy that cannot share its frames keeps its own window and
the tile says why. The default is worth revisiting once mirror has the same mileage.

How the frame crosses, and why the tile is drawn with a stretching swap chain and no shader at
all, is in [`src/GateKit.Deck/README.md`](../../src/GateKit.Deck/README.md); the game's end is
`Extender/Client/IMGUI/FrameShare.inl` in the fork, behind
`Ext.ClientRender.MirrorEnable` / `MirrorInfo`.

For playing in a tile yourself, adopt is better and always will be.

So the deck has one tile with two backings, chosen per instance and switchable at run time: adopt
where it is available, mirror everywhere else. Mirror is the one that makes the deck
cross-platform, and it costs no new mechanism — only the capture and injection that already have
to exist. (On Windows it is Direct3D 11 shared textures; the Vulkan backend of the fork has no
capture at all yet, so a game on `bg3.exe` cannot be mirrored and keeps its own window.)

The window still carries GateKit's icon and the instance's name, in both modes:
`assets/brand/logo-round.ico` — the medallion, transparent outside its ring, so it sits well on
any taskbar — with `logo.ico`, the emblem on the brand field, as the square alternative.

## A game that is not in front renders slower — and now it need not

**Built.** Watching three copies at once showed it plainly: the one you are not looking at ran at
a noticeably lower frame rate. That hurt both ways of showing a game — a tile behind another, and
every mirrored tile, since a mirrored game is never the foreground window.

It was deliberate, and the engine's own strings said so:

```
YieldOnLostFocus on  : enables framecap when window is not in focus
YieldOnLostFocus off : disables framecap when window is not in focus
```

### What the engine actually does

The Toolkit ships the same engine as DLLs that export their C++ classes by name, and five of those
exports are two or three instructions long, which is the whole mechanism:

```
BaseApp::GetFrameCap            movzx eax, word ptr [rcx+0x1C4]
BaseApp::SetLostFocusFrameCap   mov word ptr [rcx+0x1C8], dx
BaseApp::GetHasFocus            [rcx+0x1C2]
BaseApp::GetClock               this+0xE0
ls::GameClock::SetFrameCap      [this+0x18] = fps ? 1000000.0f / fps : 0
```

`BaseApp` keeps **three** sixteen-bit caps — the one asked for, the one in force, and the one to
fall back to when focus is lost — and its clock holds one number derived from the cap in force: the
microseconds a frame must last. `BaseApp::SetFrameCap` is the arithmetic between them:

```
FrameCap = fps
EffectiveFrameCap = (YieldOnLostFocus && !HasFocus)
    ? (FrameCap ? min(FrameCap, LostFocusFrameCap) : LostFocusFrameCap)
    : FrameCap
Clock.MicrosecondsPerFrame = EffectiveFrameCap ? 1000000.0f / EffectiveFrameCap : 0
```

A real game, read live, says exactly that: `FrameCap` 0, `LostFocusFrameCap` **30**,
`YieldOnLostFocus` true, `HasFocus` false, `EffectiveFrameCap` 30, 33333 microseconds. The thirty
frames a second were never the machine's doing.

Two things fall out of it, and both mean **nothing has to be hooked or patched**:

- **`LostFocusFrameCap = 0` turns the background throttle off by itself**, because zero means "no
  cap" at every step of that calculation. `YieldOnLostFocus` — a byte of `GlobalSwitches` at +0x23,
  which only the console command writes — never has to be touched. That is route 1 of the three
  this page used to list, reached through a field the Toolkit names rather than a flag nobody has
  checked; route 2, telling the game it has focus, is not needed and would have had side effects.
- **A cap that holds in the background is both caps set together**, since the smaller of the two
  wins while the window is not in front.

So `Ext.ClientRender.SetFrameCap(fps)` writes both, and `SetFrameCap(0)` lifts both. The game is a
shorter build of the engine than the Toolkit — its caps are at 0x14C, its clock's microseconds at
0xE8 — so the offsets were found by asking a running game where its own arithmetic comes out right,
and that same identity is checked before anything is ever written. A game whose layout has moved is
not written to at all. Script Extender maps none of this: `struct App` is an empty placeholder
there and `GlobalSwitches` carries no frame cap, so this is new ground.

### Measured

Game 4.76.31.656, in a background window, one copy:

| | |
|---|---|
| frames a second, throttled / after `SetFrameCap(0)` | **30.1 / 156.9** |
| a cap of 10 / 20 asked for, answered with | 9.9 / 17.7 |
| what the engine was doing by itself | `LostFocusFrameCap` 30, `YieldOnLostFocus` on |

Two copies in a deck, one held open, with the budget below:

| | |
|---|---|
| the tile held open | **139.0 frames a second** |
| the other tile, floor of 10 | **10.0** |

Both would have been pinned at 30 before. So the two questions this page asked are answered:
**the engine does honour a cap set from outside its options screen**, and a capped copy keeps
answering the bridge — but the bridge answers on the game's own tick, so a cap of *n* costs up to
*1/n* of a second of latency. Ten a second is a sensible floor; two is not.

One thing to know: the game's own options overwrite it. Changing a graphics setting makes the
engine re-apply `FrameCapEnabled` / `FrameCapFPS` from its graphic config into the same field.

### Whose games a deck takes

**Built**, and it belongs beside the other two because it is the same shape of decision. A deck
adopts a window, caps a frame rate and mutes the sound; doing all three to a copy somebody else
started is not something to do by accident, and it was found by running a deck beside another
session's game, which it duly seized.

So a deck takes **only the copies this stand started**, and that is not better manners but a
stricter default: a person playing Baldur's Gate 3 who opens a deck for an unrelated reason sees
nothing happen to their game at all. It is listed as not this stand's, the deck says once that it
is leaving it alone, and nothing is done to it. What separates one stand's copies from another's
is that each keeps its own records of the instances it made; the instance folders are shared by
everyone on the machine and say nothing about who started what.

Past it are two asks and nothing else: `gatekit deck --all`, and naming a copy with
`window maximize|mirror|adopt`, which the deck answers by saying what it has just taken and how to
give it back. That is what a loud message is for — a note about what was taken when you asked for
it to be taken, rather than an apology for taking it.

### The cap as a feature: the deck's budget

**Built.** Whatever makes the throttle go away also hands us the throttle, and that is probably the
thing that decides whether anyone but us can run several copies at all. Four games rendering flat
out need a machine most people do not have. Four games where the one you are watching runs fast and
the other three are capped at ten need far less and lose nothing: a copy nobody is looking at
exists to run a scenario or let an agent think, and it does not care how smoothly it draws.

- **A cap per copy through the API**: `gatekit window fps <instance> <n>`, the `fps` action of the
  `bg3_window` MCP tool, and `GK.SetFrameCap(n)` in a scenario. None of them needs a deck.
- **A budget the deck keeps**: the tile in view — held open, or the one with the keyboard in a grid
  — draws as fast as it likes and the rest share a floor. Change which tile is held open and the
  budget follows on the next tick. `FrameBudget` is the whole rule, written as a function from the
  tiles to a cap each with no game in it, so it is checked by ordinary tests.
- **A sensible default rather than a policy nobody asked for**: one copy on its own is never
  capped, the floor is ten a second, `GATEKIT_DECK_FPS` is `off` or `10` or `10/60`, and
  `gatekit window fps` overrules one copy until the layout next changes.
- **Nothing is left changed behind the deck's back**: a copy that leaves the deck, and every copy
  when the deck closes, gets back the throttle it had before the deck touched it. A copy left
  uncapped in the background would draw flat out for nobody.

## Sound

Mirroring costs nothing here, because sound was never attached to the window. Each copy of the
game is its own process writing to the system mixer; whether its window is embedded in a tile,
hidden, or on another screen makes no difference to what comes out of the speakers. Four copies
playing at once is the problem, and it is a problem today, with or without the deck.

The game already has the controls. `GlobalSwitches` carries **twelve sound settings**, each with
its own key and value — the sliders the options screen shows, master and music and voices and
the rest — and a flag `MuteSoundWhenNotFocused` that the game honours by itself. All of it is
reachable through the bridge, which means per-copy, per-category volume that works the same on
every platform and needs no audio API of ours.

**Built**, in two halves: the controls are `GK.Sound`, `gatekit sound` and the `bg3_sound` tool,
which write the game's own audio parameters (`docs/contracts/lua-api.md` 8); the deck's part is the
policy.

- **Only the tile you are looking at is heard.** When one tile is held open the others are
  silenced; with a grid, sound follows whichever tile has the keyboard. With a grid and nothing
  clicked, nobody is being watched in particular and **no copy is silenced** — the design's
  "nothing if none has" would have meant a deck that goes quiet the moment you click away from it,
  which is a policy nobody asked for. `SoundFocus` is the whole rule, next to `FrameBudget` and
  checked the same way.
- **It is a choice and not a law.** Every tile's caption carries a level bar and a mute; touching
  either **pins** that copy and the deck stops policing its sound, because listening to two fights
  at once is a legitimate thing to want. `GATEKIT_DECK_SOUND=off` switches the policy off
  altogether, and a copy that leaves the deck is unmuted.
- **Through the API as well**: `gatekit sound` does the twelve categories per copy, so an agent
  watching a run can quieten it without touching a window. The tile caption is deliberately only
  the master volume and a mute — a caption twenty pixels tall is not where eleven categories
  belong.

The operating system's own per-process volume — the Windows mixer, a PipeWire sink input — is a
second lever worth having for the case the game cannot answer: a copy that has hung still makes
noise, and only the system can stop it. It is a fallback, not the mechanism.

## On Linux

The stand itself is cross-platform and the deck must not change that: `up`, `down`, `exec`,
instances, hot reload and the bridge know nothing about windows.

The deck is another matter, and the answer differs by display server:

- **X11, including XWayland** — adoption is the same idea under another name (`XReparentWindow`),
  and a game running under Proton draws into an X11 window, so it should work. Unproven.
- **Wayland, natively** — a client cannot put another client's window inside its own; the
  protocol is built to forbid it. Adopt is impossible there, and there should be no pretence
  otherwise — which is exactly why mirror exists.

So on Linux the deck works in mirror mode, and gains adopt wherever the display server allows
it. The parts of the design that are only about windows are marked as such, and nothing else
may depend on them.

**The real Linux gap was not the deck**: `shot` and `key` were unimplemented there
(`LinuxProtonPlatform` refuses the window paths with a message). The fix was not an X11
screenshot path but the one this toolkit prefers anyway — press the key and read the frame
through the game's own code, in GateKit's Script Extender build, which works the same on every
platform and through any compositor. Both are now done:

- `GK.Key` injects an SDL key event into the game's own input queue, so a key needs no window and
  no focus. `PostMessage` is only the fallback for a game without the bridge.
- `GK.Shot` asks the extender's rendering backend for the frame the renderer is about to show.
  The backend copies it out of the swap chain in its present hook, before it draws any window of
  its own, so the picture is the game alone, and encodes the PNG on a thread of its own, so the
  game is not held up. `PrintWindow` is only the fallback. So far only the DX11 backend can do
  it; on the Vulkan executable the window is photographed instead, which Linux cannot do.

## Open

Whether the game's own resolution should follow the tile or letterbox inside it; what a tile
shows for a game that is loading rather than playing; and whether anyone will want a deck on
Linux, where running several games at once is rarer and the window manager already tiles.

**What is left on the deck, in the order it is worth doing** - all of it after the first
publication, since none of it is what the deck is published for:

1. **HDR10 shown on an SDR screen.** A pixel shader in the deck doing what `ToRGBA8` does in the
   extender's `DX11.inl`. It can be checked on any screen by comparing the shader's output with
   `GK.Shot`'s PNG of the same frame, so it does not wait for hardware. Narrow: it only bites
   when the game is on an HDR screen and the deck on an SDR one at once.
2. **Vulkan sharing**, so a copy on `bg3.exe` can be mirrored. The fork's Vulkan backend has no
   capture at all, so this is a new capability rather than a port of the DX11 one.
3. **Linux**, which needs a Linux install on hardware and cannot be done from here.

Four more, each found by building the rest:

- **What mirroring really costs**, to a number rather than "less than this machine can measure".
  It wants a quiet machine and a longer run.
- **HDR10 shown on an SDR screen** is untested: the frame is passed through and the tile's swap
  chain put in the PQ colour space, which is right only when the deck is on an HDR screen too. The
  fix for the other case is a pixel shader doing what `ToRGBA8` in `DX11.inl` does, behind the
  same seam.
- **The game's own options overwrite a cap.** Changing a graphics setting makes the engine
  re-apply its graphic config into the same field. Nothing watches for that yet; the deck's next
  change of layout sets it again.
