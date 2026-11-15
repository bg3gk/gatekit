# The deck

One window that holds every running game copy. Why it is shaped this way:
[docs/design/deck.md](../../docs/design/deck.md). The commands' contract:
[docs/contracts/cli.md](../../docs/contracts/cli.md) (`deck`, `window`).

```
gatekit deck                      open it (from a terminal)
gatekit-deck                      open it from anything else: a shortcut, a script, a tool
gatekit window list               every copy: name, label, pid, tile
gatekit window maximize deck1     one copy fills the deck and keeps its place while it restarts
gatekit window grid               everything back in the grid
gatekit window label deck1 "Nautiloid run"
gatekit window release deck1      its own window back, out of the deck (and left alone after)
gatekit window mirror deck1       draw it from the frames it shares; its own window goes out of sight
gatekit window adopt deck1        its own window back in the tile
gatekit window fps deck1 10       how fast one copy may draw; 0 for no cap
gatekit deck close                close a running deck; every window goes back
```

| Setting | Values |
|---|---|
| `GATEKIT_DECK_BACKING` | `adopt` (the default) or `mirror`, for new tiles |
| `GATEKIT_DECK_CONCEAL` | a mirrored copy's own window: `offscreen` (the default), `hidden`, `none` |
| `GATEKIT_DECK_FPS` | `off`, a floor for the tiles not in view (`10`, the default), or a floor and a cap for the one in view (`10/60`) |
| `GATEKIT_DECK_SOUND` | `off` switches the sound policy off |
| `GATEKIT_DECK_ADOPT` | `all` is `gatekit deck --all`: take copies another home made too |

## Opening it

`gatekit deck` starts the deck and comes straight back; the deck runs on and writes to `deck.log`
in the home's `state/deck`. `gatekit-deck` is the same command built for the window subsystem: a
console program started without a console gets one, and a new console takes the keyboard the
moment it appears - the thing the deck exists to stop.

Either way the deck copies its own folder into the home's `cache/run` and runs from there,
detached, with handle inheritance off: the repository can be rebuilt while the deck is up, the
terminal that started it is not held, and it gets no console on the way.

The deck is a plain Win32 window - a window class, a message loop, `SetParent`, `SetWindowPos` and
a caption drawn per tile, as P/Invoke in `Windows/`. WinForms or WPF would need a
`net10.0-windows` target, which would spread up to `GateKit.Cli` and stop the command line building
on Linux.

## Focus

Three things keep the deck from taking the keyboard, and leaving out any one brings the problem
back: its window is created `WS_EX_NOACTIVATE`, shown with `SW_SHOWNOACTIVATE`, and nothing
activates it - no `SetForegroundWindow`, every move `SWP_NOACTIVATE`. The style is dropped the
first time you click a tile, the one moment activation is wanted.

Every time a deck opens it compares the foreground window before and after showing itself and
warns loudly if it became the foreground. No unit test can assert a caret, so when you change any
of this, check it by hand: type in another window, run `gatekit deck` and then
`gatekit --instance <yours> up`, and watch the caret stay where it was.

## How it works

- **The deck finds the games.** Several times a second it lists the game processes, maps each to
  the copy whose folder it runs from, and finds its window (class `SDL_app`, by process id,
  children of other windows included - a game already in a deck is a child window, and `shot`,
  `key` and `exec` must find it all the same). A deck started after the games takes them; a game
  started without a deck has its own window.
- **Adoption** is `SetParent`, the frame and `WS_POPUP` cleared and `WS_CHILD` set, then
  `SetWindowPos` with `SWP_NOACTIVATE`. The game is never activated; it sees its window move and
  lays itself out again.
- **Tiles keep their place.** A tile belongs to a copy, not to a process: when the game exits the
  tile stays, says so, and takes the next window that copy opens. A tile empty for ten minutes is
  dropped, unless it is the one held open.
- **Holding one open** puts the other tiles exactly behind it: real windows of a real size, still
  rendering, still readable by `shot` - simply covered. They are not parked outside the deck,
  because `PrintWindow` fails on a child window that lies wholly outside its parent.
- **A tile's content** comes from an `ITileBacking`, chosen per tile and switchable while the deck
  runs: `adopt` puts the game's own window in it, `mirror` draws the frames the game shares.
- **Keyboard.** The deck has the keyboard until you click a tile. A click gives that game the focus;
  if that does not take, the deck forwards the key messages it receives to that tile.
- **Everything is given back** - on close, on `window release`, and when a game's window goes
  away: the window gets its old style, parent and place.
- **Whose games.** A deck takes only the copies its own home made (`Adoption`); anything else is
  listed by `window list` and left alone until it is named or `--all` is given.

## The mirror backing

The game's end is in GateKit's Script Extender build (`Extender/Client/IMGUI/FrameShare.inl` in
the fork): in the present hook, before anything of the extender's is drawn, the frame is copied
with one `CopyResource` into a Direct3D 11 texture created `SHARED_NTHANDLE | SHARED_KEYEDMUTEX`
and given a name. A block of shared memory (`Local\GateKitFrameShare_<pid>`) carries that name,
the frame's size and format, the adapter, a counter of copies and a counter of every present.
`Ext.ClientRender.MirrorEnable(true)` turns it on and `Ext.ClientRender.MirrorInfo()` reads it;
the deck asks over the bridge.

The deck opens the block by process id, opens the texture with
`ID3D11Device1::OpenSharedResourceByName` on the adapter named, and watches the counter. Both ends
take the keyed mutex with a timeout of 0 (the game) and 1 ms (the deck), so neither waits on the
other: a frame that cannot be had this instant is skipped.

Each mirrored tile is a child window whose swap chain is created at the game's resolution with
`DXGI_SCALING_STRETCH`, so showing a frame is `CopyResource` into the back buffer and `Present`,
and DXGI scales it - no shader, no vertex buffer, no graphics library (`MirrorNative.cs`). An sRGB
frame is copied into the plain twin of its format; an HDR10 frame is passed through and the tile's
swap chain put in that colour space.

One thread shows the frames of every tile: it draws only a tile whose game has presented since,
at most 60 a second, and every ten seconds writes to the log what each tile reaches and costs.

A mirrored copy's own window is put out of sight by `GATEKIT_DECK_CONCEAL`: `offscreen` moves it
past every screen and makes it a tool window, still shown as far as the game is told; `hidden`
hides it, which the game renders through, but then GateKit's own window fallbacks, which look for
a visible window, cannot find it. Whatever was done is undone when the tile is dropped, the deck
closes, or the tile goes back to adopt.

## The frame budget and the sound

The tile in view - held open, or the one with the keyboard in a grid - draws as fast as it likes;
the rest share the floor of `GATEKIT_DECK_FPS`. One copy on its own is never capped. The deck sets
each copy's cap over the bridge (`GK.SetFrameCap`), from a thread of its own and only when it
changes; a copy that leaves the deck gets back the throttle it had. `FrameBudget` is the rule, with
no game in it. A copy that refuses a cap is named once in the log, after twenty seconds in its tile.

Sound follows the same view through the game's own settings (`GK.Sound`): the tile in view is
heard, the rest are muted; with a grid and nothing clicked nobody is silenced. Touching a tile's
level bar or mute pins that copy, and the deck leaves its sound alone. `SoundFocus` is the rule.

## What it will not do

- **Survive being killed.** Windows destroys the children of a window it destroys, and a process
  killed outright cannot give anything back first. Close the deck by its own button, with Ctrl+C in
  its terminal, with `gatekit deck close`, or with `window release`.
- **Run twice.** A second deck refuses to start; the first already holds the windows.
- **Run off Windows.** `window list` and `label` work anywhere; holding windows is behind
  `IGamePlatform.CanHostWindows`.
- **Set more than the master volume and a mute.** The other categories are `gatekit sound`'s.
- **Change how a game is read.** `shot`, `key`, `exec` and the bridge work on a copy behind another,
  out of view or in no deck; the `deck` test (`gatekit --instance <copy> test tests -k deck`) holds it.
