# A lead: input made by us - keyboard, mouse, gamepad - and the interface it calls up

Not built. This file holds what is known and what the owner asked for, so the work can start
from it. The owner set it aside on 16 November 2026. It belongs with the open item "Input" in
[open-items.md](../open-items.md), which it does not replace.

## What was asked

- One subsystem for made-up input of every kind: keyboard, mouse with smooth motion, gamepad
  with its sticks, quick to send where it is needed.
- Open to modders too, marked as not ready for production: a line in the log on first use, in
  the manner of the Script Extender's own warnings, and a sentence in each call's description,
  both pointing at the toolkit's reading and pressing of windows by handle as the right way.
- The item "Input" of the open items, as written there: which interface is on, the switch each
  way, why any input of the mouse throws it back, a verb that holds the interface wanted, where
  in the machine code the switch lives, the lock that keeps real input out, and the tests
  `tests/gamepad/state`, `switch-gameplay`, `switch-menu`, `tests/input/lock`.
- The owner's condition that the first run with a real device be made with him there is
  withdrawn: it was a guard against having to work by hand.
- His scripts of a virtual gamepad may be used if the machine code proves hard to read: they are
  outside the repository, in his shields project (`scripts/webpad.py`, `scripts/pad.sh`). They
  stay there; a device made by them is seen by every running copy at once.
- His rules that bind this work: an API never rests on screen coordinates, which are allowed as
  an instrument of research only; the fork opens what the game does and adds no logic of its own.

## What is known (each with what it stands on)

- **Injected mouse and keys reach the game** with the build `v34-ui` of the fork: an injected
  motion names the game's window, so the game takes its position; a hover opened the game's own
  tooltip and a click opened a colour panel, with the real pointer unmoved. Injected keys raise
  the extender's input events with a mark that they were injected.
- **Why a bare injection did nothing before:** the game's window manager
  (`sdl::SDLWindowManager::Update` in the registry) takes a motion's position only from an event
  that names its window.
- **A press and its release need a frame between them.** A real motion inside the window
  overwrites an injected position.
- **The table of bindings read through the extender is unreliable in one of its two forms:** the
  form the toolkit's press-by-event-name used gave a wrong second binding for many events (a
  press meant for the pin of a tooltip sent Enter); the other form reads right. This is a defect
  of the toolkit to mend first.
- **Listeners of the toolkit's own windows now hear injected input** and may need to ignore it.
- **Nothing is known yet of the gamepad:** not how the game opens a controller, not what decides
  which interface is shown, not what it rebuilds on a switch.

## Where to dig

1. **Where input enters the game**, by the instruments: the window manager's update, the input
   manager's raw-to-event step, the device ids of keyboard, mouse and controller.
2. **A controller inside the process.** An idea from an earlier note, not verified: the input
   library the game ships has virtual joysticks; attached from inside the extender, the game
   would see a controller plugged in and open it as a real one - one for each copy, and no
   driver. To verify first: that the game's shipped library exports those calls, and that the
   game switches to its controller interface for such a device. A second road, deeper: the
   engine's own queues of injected input events.
3. **The switch of the interface:** what the game reads to decide, what it tears down and builds
   when it switches, and how to hold the wanted one by removing the cause of the switch rather
   than mending its effects.
4. **Smooth motion:** a path sent over frames by the same injected events; sticks as values over
   time.
5. **The lock:** real input kept out at the same place where made-up input is let in, with a
   state that can be read and undone.

## What exists to start from

- The research instrument `GK.Cheat.Pointer` (a pointer inside the game) and the notes of the
  packages that built it and the fork's part: the session's notes folder, `night/U1/` and
  `night/G2/`.
- The fork's section "Injected input" in its `GATEKIT.md`.
