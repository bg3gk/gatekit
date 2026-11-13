# The deck

One window that holds every running game. `docs/design/deck.md` is the design; this is how it is
built and what it does not do.

```
gatekit deck                      open it (from a terminal)
gatekit-deck                      open it from anything else: a shortcut, a script, a tool
gatekit window list               every copy: instance, label, pid, tile
gatekit window maximize deck1     one copy fills the deck and keeps its place while it restarts
gatekit window grid               everything back in the grid
gatekit window label deck1 "Nautiloid run"
gatekit window release deck1      its own window back, out of the deck (and left alone after)
gatekit window mirror deck1       draw it from the frames it shares; its own window goes out of sight
gatekit window adopt deck1        its own window back in the tile
gatekit window fps deck1 10       how fast one copy may draw; 0 for no cap
gatekit deck close                close a running deck; every window goes back
```

## Two ways to open it, and why there are two

`gatekit deck` is the one to type. It starts the deck and comes straight back; the deck runs on
without it, and writes to `deck.log` beside its state rather than to the terminal, which is yours.

`gatekit-deck` is the same thing for everything that is not a terminal - a shortcut, a script, a
tool. `gatekit` is a console program, and Windows gives a console program a console window
whenever it is started without one. That window is not the deck, nobody reads it, and a new
console **takes the keyboard the moment it appears** - the exact thing the deck exists to stop.
It can be hidden from inside, and that still leaves a flash of about forty milliseconds, because
the window exists before the program's first instruction runs.

A program built for the window subsystem never gets one, and that is all `gatekit-deck` is: no
logic of its own, the same command, no console at any point. Measured by reading the foreground
window every 20 ms for ten seconds across opening a deck: with `gatekit-deck`, every single
sample named the window that already had the keyboard.

## The deck does not run out of `bin`

Whichever way it is opened, the deck copies its own folder to `%LOCALAPPDATA%\GateKit\run\deck`
and runs from there, detached, with handle inheritance off. Three reasons, all of them learned
the hard way: the repository can be rebuilt while the deck is up (a running program keeps its
assemblies locked, and the build then fails on files rather than on code); the deck does not hold
the pipes of the terminal that started it, which used to leave that terminal looking hung for as
long as the deck was open; and it gets no console of its own on the way.

## Why a plain Win32 window and not WinForms or WPF

Both would work, and both cost the same thing: they need a `net10.0-windows` target framework,
which spreads up the reference chain - `GateKit.Cli` references this project, so the command line
itself would stop building on Linux, and `dotnet build` of the solution would need the Windows
targeting pack. WPF adds a second runtime (`Microsoft.WindowsDesktop.App`) on top of that.

What the deck actually needs is a frame around other people's windows: a window class, a message
loop, `SetParent`, `SetWindowPos`, and a caption drawn per tile. That is about two hundred lines
of P/Invoke in `Windows/`, in the same style as `GateKit.Platform`, and it leaves this project a
plain `net10.0` library that builds and runs its tests on Linux like every other one. A user
interface framework would be worth its weight if the deck grew real controls; until then it is a
dependency bought for nothing.

## Checking that focus-stealing cannot come back

The deck exists because a new game threw its window over whatever you were doing. The deck itself
must never do that, and it is the kind of thing that comes back quietly, so it is checked in two
places:

- **Every time a deck opens**, it compares the foreground window before and after showing itself
  and prints a loud warning if it became the foreground. Nothing in the deck ever calls
  `SetForegroundWindow` on itself except from a click (see below).
- **By hand, once, when you touch any of this** (there is no way to assert a caret from a unit
  test): start typing in another window, run `gatekit deck` and then `gatekit --instance <yours>
  up`, and watch the caret. It must stay where it was, and the deck must appear behind. Measured
  the same way during the work: a loop reading `GetForegroundWindow()` every 150 ms while a deck
  opened and two games started, which must never name the deck or a game.

Three things make it hold, and leaving out any one of them brings the problem back: the deck
window is created `WS_EX_NOACTIVATE`, it is shown with `SW_SHOWNOACTIVATE`, and nothing activates
it - no `SetForegroundWindow`, no raising, and every move is `SWP_NOACTIVATE`. The style is
dropped the first time you click a tile: that click is the one moment activation is wanted, and it
comes from you.

## How it works

- **The deck finds the games; the stand does not tell it.** Every 400 ms the deck lists the game
  processes, maps each to the instance whose folder it runs from, and looks for its window
  (class `SDL_app`, by process id, children of other windows included - a game already in a deck
  is a child window, and `shot`, `key` and `exec` must find it all the same). So a deck started
  after the games adopts them, and a game started without a deck simply has its own window.
- **Adoption** is `SetParent` plus clearing `WS_POPUP` and the frame and setting `WS_CHILD`, then
  `SetWindowPos` with `SWP_NOACTIVATE`. The game is never activated and never told it was clicked;
  it sees its window move and re-lays itself out.
- **Tiles keep their place.** A tile belongs to an instance, not to a process: when the game
  exits, its tile stays, says so, and takes the next window that instance opens. A tile that has
  been empty for ten minutes is dropped, unless it is the one held open.
- **Holding one open** puts the other tiles exactly where the held one is, at the back of the
  pile: real windows of a real size, still rendering, still readable by `shot` - simply covered.
  Nothing is hidden or minimized, because the rest of GateKit must never depend on a window being
  visible. (Measured, and the reason they are not parked outside the deck instead: `PrintWindow`
  fails on a child window that lies wholly outside its parent, which would have broken `shot` on
  the copy nobody can see - the very rule the deck must not break.)
- **One tile, two ways to fill it.** A tile takes its content from an `ITileBacking`, chosen per
  tile and switchable while the deck runs (`window mirror|adopt NAME`; `GATEKIT_DECK_BACKING` is
  the default for new tiles, and it is `adopt`). `adopt` puts the game's own window in the tile.
  `mirror` never touches that window: the game copies its finished frame into a shared texture on
  the graphics card and the deck draws that. See below.
- **Keyboard**: the deck has the keyboard until you click a tile. A click gives that game the
  focus (its thread's input queue is attached for the moment it takes to say so); if that does not
  take, the deck forwards the key messages it receives to that tile - the same posted messages
  `gatekit key` uses.
- **Everything is given back**: on close, on `window release`, and when a game's window goes away,
  the window gets its old style, parent and place back.

## The mirror backing: how the frame crosses, and why it is drawn the way it is

The game's end is in GateKit's Script Extender build, `Extender/Client/IMGUI/FrameShare.inl`: in
the present hook, where the frame is finished and nothing of the extender's has been drawn over
it, it is copied with one `CopyResource` into a Direct3D 11 texture created
`SHARED_NTHANDLE | SHARED_KEYEDMUTEX` and given a name. A small block of shared memory
(`Local\GateKitFrameShare_<pid>`) carries that name, the frame's size and format, the adapter it
lives on, a counter of copies and a counter of **every** present whether mirroring or not - which
is the thing to measure a cost with. `Ext.ClientRender.MirrorEnable(true)` turns it on and
`Ext.ClientRender.MirrorInfo()` reads it; the deck asks over the bridge, like everything else.

The deck opens the block by the game's process id, opens the texture with
`ID3D11Device1::OpenSharedResourceByName` on the adapter the block names, and watches the counter.
Both ends take the keyed mutex on key 0 with a timeout of 0 (the game) and 1 ms (the deck), so
neither ever waits on the other: a frame that cannot be had this instant is skipped, not queued.

**The drawing path is a swap chain per tile, and no shader at all.** Each mirrored tile gets a
child window; its swap chain is created at the *game's* resolution with `DXGI_SCALING_STRETCH`, so
showing a frame is `CopyResource` from the shared texture into the back buffer and `Present`, and
DXGI does the scaling on the way to the screen. Nothing else was needed: no vertex buffer, no
sampler, no runtime HLSL compiler, no graphics library in the dependency list - about 200 lines of
vtable calls in `MirrorNative.cs`, in the same style as the rest of the Win32 here. An sRGB frame
is copied into the plain twin of its format, which is what `CopyResource` allows; an HDR10 frame is
passed through as it is and the tile's swap chain is put in that colour space, which is the same
thing the compositor does for the game's own window on the same screen. (The alternative, a
fullscreen triangle with a pixel shader, buys per-tile colour conversion and letterboxing in the
shader, and it can be added behind the same seam if an HDR game ever has to be shown on an SDR
deck.)

What it costs the game, measured by switching mirroring on and off under the same game running
flat out (eight alternating fifteen-second samples, one tile held open, a second game beside it):
**149.2 against 149.9 frames a second** - indistinguishable - or 2.7 % of the frame time with the
settling sample dropped. The copy's own time on the rendering thread, 0.4-0.7 ms, is an upper
bound rather than the cost: the card does the copy while the frame is on its way out.
`docs/design/deck.md` has the tables.

The frames are shown by one thread for every tile. It reads a word of shared memory per tile and
draws only a tile whose game has presented since the last one drawn, at 60 a second at most; a
round that finds nothing costs those reads and a 3 ms sleep. Every ten seconds it prints what each
tile is reaching and costing, which is where the numbers in `docs/design/deck.md` come from.

**A mirrored copy's window is not looked at, so it is put out of sight** - `GATEKIT_DECK_CONCEAL`:
`offscreen` (the default) moves it past every screen and makes it a tool window, so it is on no
screen and in no taskbar while staying, as far as the game is told, a shown window; `hidden` hides
it outright, which BG3 does keep rendering through (measured - `docs/design/deck.md`), but then
GateKit's own window fallbacks cannot find it, because they look for a *visible* window. `none`
leaves it alone. Whatever was done is undone when the tile is dropped, the deck closes, or the
backing is switched back to adopt.

## Whose games a deck takes

A deck does three things to a game: takes its window out of the desktop, caps how fast it draws,
and mutes it. So it takes **only the copies this stand started**. A person who is playing Baldur's
Gate 3 and opens a deck for an unrelated reason sees nothing happen to their game: it is listed by
`window list` as "not this stand's", the deck says once that it is leaving it alone, and that is
all.

What separates them is that every stand keeps its own records of the instances it made - the
instance folders are shared by everyone on the machine and say nothing about who started what.

Two ways past it, and both of them are somebody asking: `gatekit deck --all`
(`GATEKIT_DECK_ADOPT=all`), and naming a copy with `window maximize|mirror|adopt`, which the deck
answers by saying out loud what it has just taken and that `window release NAME` gives it back.
`Adoption` is the whole rule, with no window and no game in it.

## The frame budget: what makes several copies affordable

A copy whose window is not in front draws at **thirty frames a second**, and that is the engine
throttling itself on purpose, not the machine giving up. It hits a deck twice over: at most one
tile is ever in front, and a mirrored copy never is.

GateKit's Script Extender build turns the throttle off and hands the knob back
(`Ext.ClientRender.SetFrameCap`, `docs/contracts/lua-api.md` 9), and the deck spends it:
**the tile in view draws as fast as it likes and the rest share a floor.** In view means the tile
held open, or - in a grid - the one with the keyboard. One copy on its own is never capped.

`FrameBudget` is the whole rule and has no game in it: tiles in, a cap each out, which is the only
way to be sure of it without a machine full of games. The deck asks each copy over the bridge, from
a thread of its own, and only when that copy's cap has changed. A copy leaving the deck gets back
the throttle it had before the deck touched it - a copy left uncapped in the background would draw
flat out for nobody.

`GATEKIT_DECK_FPS` is `off`, a floor (`10`, the default), or a floor and a cap for the tile in view
(`10/60`). `gatekit window fps NAME N` overrules one copy until the layout next changes.

Measured on this machine, two games, one held open: **139 frames a second for the one being
watched and exactly 10 for the other**. Both would have been pinned at 30 before.

**It does not fail silently.** The frame cap is the most fragile thing GateKit knows about the
engine - fields of `BaseApp` at offsets a game patch can move - and when it stops working nothing
breaks: the budget simply does nothing and several copies are slow again, which is the kind of
fault nobody traces back. So a copy that refuses a cap is named once in the deck's output with
what it costs, after twenty seconds in its tile (a game that is still loading has refused
nothing), and `scenarios/framecap.lua` asserts the whole chain against a real game - every field
written, the game really drawing at the rate it was given, and the background throttle really
lifted.

## Sound follows what you are looking at

The same rule, through the game's own twelve volume settings (`GK.Sound`): the tile in view is
heard, the rest are muted. With a grid and nothing clicked, nobody is being watched in particular
and no copy is silenced - silencing everything would be a policy nobody asked for.

It is a choice and not a law. Every tile's caption carries a level bar and a mute; touching either
pins that copy and the deck stops policing its sound, because listening to two fights at once is a
legitimate thing to want. `GATEKIT_DECK_SOUND=off` switches the whole thing off, and a copy leaving
the deck is unmuted.

## What it will not do

- **Kill the deck process and the games lose their windows.** Windows destroys the children of a
  window it destroys, and a process killed outright cannot give anything back first. Close the
  deck by its own button, with Ctrl+C in its terminal, or with `window release`.
- **One deck per machine.** A second one refuses to start, since the first already holds the
  windows.
- **Windows only, for now.** `gatekit window list` and `label` work anywhere; adopting windows is
  behind `IGamePlatform.CanHostWindows`, and on Linux the window manager does the tiling.
- **Hold a deck's own volume.** The deck sets the game's master volume and mutes it; the eleven
  other categories are `gatekit sound`'s business, and so is anything subtler than a slider.
- It changes nothing about reading a game: `shot`, `key`, `exec` and the bridge work on a copy
  that is behind another, out of view or in no deck at all. `scenarios/deck.lua` is there to keep
  that honest.
