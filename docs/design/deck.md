# The deck: every game in one window

Why the deck is shaped the way it is. How it is built and used: [src/GateKit.Deck/README.md](../../src/GateKit.Deck/README.md).

Running several games at once is one of the reasons GateKit exists - an agent plays in one, a test
runs in another, you watch a third. What does not work without help is *living* with them: each
new game throws its own window over whatever you were doing and steals the keyboard, Script
Extender adds a console window per game, the windows look alike so the wrong one gets closed, and
several games drawing flat out need a machine few people have.

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

**Take the window, not the focus.** A game started by GateKit never comes to the front and never
takes the keyboard. It appears in its tile, and whatever you were typing in keeps the caret.

**Name every copy.** A tile carries the copy's name, a label you can set, and the game's process
id. You should never have to guess which window is which, and `gatekit down` on the wrong copy
should be impossible to do by accident.

**Tile by itself.** With nothing held open the tiles fill a grid. A tile keeps its place when its
game exits, so the layout does not jump around while you are watching.

**Hold a tile open.** A copy you are watching keeps the whole deck while its game restarts - a
crash, a reset, a new `up` - and takes the new window when it appears. A run you are watching
should not throw your layout away because the game blinked.

**Change nothing about reading the game.** Logs, pictures, keys and the bridge work exactly the
same for a copy that is behind another, in a tile that is not visible, or in no deck at all. This
is the rule the whole toolkit holds to, and the deck must not quietly break it.

**No Script Extender console windows.** The extender's output goes to its log files, and
`gatekit logs` reads those.

**Degrade, never break.** When a tile cannot be filled - a driver, a full-screen mode, a patch -
the copy runs in its own window, the tile says where it went, and everything else works. The deck
is a convenience, never a dependency.

**The API is the point, not the window.** Everything the deck does is a `gatekit window` command
and an MCP tool, so an agent can say "keep my run visible" and a person can rearrange without the
agent noticing. The deck is a host program of its own: the toolkit starts games whether a deck is
running or not, and a deck started later takes the games already running.

## Two ways to fill a tile

**Adopt** takes the game's own window and puts it in a tile (`SetParent`, the child style, every
move without activation). The game renders as it always does, and a click gives it the keyboard
directly. It needs nothing of the game, which is why it is the default. It is as portable as
window embedding is: Windows, X11 in principle, never Wayland.

**Mirror** does not touch the window at all. The game hands its finished frame to the deck and the
deck draws it; a key goes back through the game's own input. Nothing in that is
operating-system specific: it works under any compositor and for a game whose window is
minimised.

### Mirror is not a stream of screenshots

A picture taken over and over would be unbearable: almost all of a capture's cost is moving pixels
from the graphics card into memory and encoding them. Every screen recorder avoids that by never
bringing the pixels back. GateKit's Script Extender build already sits in the game's present call,
where the finished frame is; it copies the frame on the card into a **shared texture**, and the
deck draws that texture. Nothing crosses into system memory and nothing is encoded - a GPU-to-GPU
copy of one frame.

That makes mirror the better mode in one way adopt cannot match: **the game's window never has to
be seen.** It still needs one - a swap chain is attached to a window - but it can be put out of
sight. No window to steal focus, nothing flashing up when a game starts, no row of identical icons
in the taskbar, and tiles that can be scaled freely because they are textures rather than someone
else's window.

Two things had to be true before believing it, and both are measured:

- **The game keeps rendering when its window is hidden.** It does, at full rate, and a hidden copy
  still answers the bridge, takes keys and gives pictures from inside the game. The deck still
  moves a mirrored copy's window off every screen rather than hiding it, by default, because
  GateKit's own window fallbacks look for a visible window.
- **The copy costs the game little.** Switching mirroring on and off under a game running flat out
  moved its frame rate by a few percent at most. The copy's time on the rendering thread is an
  upper bound, not the cost: the card copies while the frame is on its way out. The deck's log
  prints what every tile reaches and costs, so the claim can be checked on any machine.

Mirror is built and is not the default, because adopt needs nothing of the game and is better for
playing in a tile yourself, while mirror asks for something only GateKit's Script Extender build on
the DirectX 11 executable can give. A copy that cannot share its frames keeps its own window and
the tile says why. The Vulkan executable has no frame sharing yet.

## The background throttle, and the budget made of it

A game whose window is not in front draws at thirty frames a second. That is the engine throttling
itself on purpose, and it hits a deck twice: at most one tile is ever in front, and a mirrored copy
never is.

The engine keeps three caps - the one asked for, the one in force, and the one to fall back to when
focus is lost - and its clock holds the microseconds a frame must last, derived from the cap in
force. The cap in force is the smaller of the asked-for and the lost-focus caps while the window is
not in front, and zero means "no cap" at every step. Two things follow, and both mean nothing has
to be hooked or patched:

- **A lost-focus cap of zero turns the throttle off by itself.** The console switch that decides
  whether to throttle never has to be touched, and the game never has to be told it has focus,
  which would have had side effects.
- **A cap that holds in the background is both caps set together.**

So `GK.SetFrameCap(fps)` writes both, and `GK.SetFrameCap(0)` lifts both. The engine's fields were
found by asking a running game where its own arithmetic comes out right, and that identity is
checked before anything is written: a game whose layout has moved is not written to at all.
Because a failure here breaks nothing visible - several copies are merely slow again - the
`framecap` test asserts the whole chain against a real game, and a deck names a copy that refuses
a cap.

What lifts the throttle also hands over the knob, and that is what decides whether anyone can run
several copies at all. **The tile in view draws as fast as it likes and the rest share a floor**:
a copy nobody is looking at exists to run a test or let an agent think, and does not care how
smoothly it draws. One copy on its own is never capped; a copy that leaves the deck gets back the
throttle it had, because a copy left uncapped in the background would draw flat out for nobody.
The rule is written as a function from the tiles to a cap each, with no game in it, so it is held
by ordinary unit tests. A cap of *n* also costs up to *1/n* of a second of the bridge's latency,
since the bridge answers on the game's tick: ten a second is a sensible floor; two is not.

## Whose games a deck takes

A deck takes a game's window out of the desktop, caps how fast it draws and mutes it. Doing that
to a copy somebody else started - another person, another session, a game being played - is not
something to do by accident. So **a deck takes only the copies its own home made**: a person who
opens a deck for an unrelated reason sees nothing happen to their game. A copy made through a home
has its record in that home's state (`state/instances/<copy>`), which is what separates them; the
copies' folders are shared by everyone on the machine and say nothing about who started what. The two ways past it
are both somebody asking: `gatekit deck --all`, and naming a copy with a `window` command, which
the deck answers by saying what it has just taken and how to give it back.

## Sound

Each copy is its own process writing to the system mixer, so four copies playing at once is a
problem with or without a deck. The game already has the controls: its own sound settings,
per category, reachable through the bridge (`GK.Sound`), which works the same on every platform and
needs no audio interface of ours.

**Only the tile you are looking at is heard.** With nothing clicked in a grid, nobody is being
watched in particular and no copy is silenced - a deck that goes quiet the moment you click away
from it would be a policy nobody asked for. It is a choice and not a law: touching a tile's level
or mute pins that copy and the deck stops policing it, because listening to two fights at once is
a legitimate thing to want. The rule is a function with no game in it, held by unit tests like the
budget's.

The operating system's own per-process volume is a second lever for the one case the game cannot
answer - a copy that has hung still makes noise - and is a fallback, not the mechanism.

## On Linux

Starting copies, the bridge, hot reload and tests know nothing about windows. The deck depends on
the display server: under X11, including XWayland, adoption is the same idea under another name,
and a game under Proton draws into an X11 window; natively under Wayland a client cannot put
another client's window inside its own, so adoption is impossible there - which is why mirror
exists. Keys and pictures do not wait for the deck: `GK.Key` puts a key into the game's own input
queue and `GK.Shot` takes the frame inside the game, so neither needs a window on any platform.
On the Vulkan executable a picture still falls back to photographing the window.

## Open

- Whether the game's own resolution should follow the tile or letterbox inside it, and what a tile
  shows for a game that is loading rather than playing.
- An HDR10 frame on an SDR deck: the frame is passed through and the tile's swap chain put in the
  HDR colour space, which is right only when the deck is on an HDR screen too. The fix is a pixel
  shader behind the same seam.
- Frame sharing for the Vulkan executable, which the fork's Vulkan backend does not have.
- The game's own options overwrite a cap: changing a graphics setting re-applies the configured
  cap, and nothing watches for it; the deck's next change of layout sets it again.
- Linux, which needs a bare-metal install to be tried on.
