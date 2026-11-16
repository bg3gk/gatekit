# The weapon in hand

When the game draws a weapon and puts it away, what holds it out, what tells that it has changed,
and the player's own request. Tests: `tests/combat/weapon/`. Row: `weapon_state` (Server/Act.lua,
from the game's `esv::unsheath::StateChangedEventOneFrameComponent`).

## The rule (bg3.wiki and wider)

The wiki has no page on drawing and sheathing; the game's own key list names the action
"Sheathe/Unsheathe Weapon" (localisation handle h4524f746g7d30g459bg97beg8f933a3a5dae). Players'
reports and the owner's observation: a fight draws the weapon and its end puts it away; an attack
out of combat draws it and the game puts it away after the attack; toggled quickly, the weapon
seems to skip its animation and appear on the back at once. Not read yet: forum threads on the
toggle - a wish.

## The mods

None of the study mods touches `esv::unsheath` or `eoc::unsheath` (`gatekit mod usage` over the
home's study mods, searched for "unsheath"); they use `Osi.SetWeaponUnsheathed` where they use
anything. A mod for weapon visibility would be the next to read (none downloaded).

## Script Extender

Declares `eoc::unsheath::StateComponent` (`MainHandWeapon`, `OffHandWeapon`, `State`:
`stats::UnsheathState`) and the server's family: `UserRequestComponent` (a counter of the player's
requests in process; its Lua label is `ServerUnsheathUserRequestPending` - `ServerUnsheathUserRequest`
names a one-frame component), the client's `ecl::unsheath::VisualStateComponent` (`State`, `State2`:
what the model shows; read through Lua and from memory at its declared offsets alike, and with no
replication event), `DefaultComponent` and `DefaultChangedOneFrameComponent` (the state the
player asked for), `StateChangedEventOneFrameComponent` (`OldState`, `NewState`),
`CombatJoiningComponent`, `CombatLeavingComponent` (a float timer, said by the reading of the code
in the coverage data to be set to 8 s of game time), `SpellAnimationLifetimeComponent`,
`SpellAnimationConcludedComponent`, `ScriptOverrideComponent`, `EquipmentChangedOneFrameComponent`,
`AnimationRequestOneFrameComponent`. Every field read here read as declared. `DefaultComponent` is
not an `ExtComponentType` label of this build (subscribing to it by that name fails); not pursued.

## The instruments (component subscriptions on the server, rows with ticks)

- **The player's request is a key.** The game's toggle is the input event `ToggleCombatMode` (its
  description is the "Sheathe/Unsheathe Weapon" handle); the profile's binding is read from the
  client's input manager (`InputDefinitions` for the event's id, `InputScheme.InputBindings[1][id]`
  for its key - U by default). Pressed, it makes `esv::unsheath::UserRequestComponent`
  (`ServerUnsheathUserRequestPending`) on the character, then
  `DefaultChangedOneFrameComponent`; two ticks later the request component goes and the state
  changes (`StateChangedEventOneFrameComponent`), with an `AnimationRequestOneFrameComponent`.
  The logical state changes at once; the animation is requested and does not hold the logic.
  The toggle draws the set in hand (`eoc::WeaponSetComponent`): the ranged state with the ranged
  set, the melee state with the melee set; the set's own key (`ToggleWeaponSet`) switches the set
  and leaves the weapon away - but now and then the game does not act on that key, injected through
  the game's input system, with nothing in the client's state to show why (a suspected bug; the
  test stages the other set instead).
- **In a fight the weapon is not put away.** On the host's turn, in a fight it began, the key makes
  no request component and no change (seen twice), and `Osi.SetWeaponUnsheathed` to put it away is
  not applied either (`GK.Cheat.Sheathe`: "still out" after 4 s). Not in a test: an absence needs an
  event after it to be shown within a step.
- **Toggled quickly** (twice, 0.3 s apart): two complete changes, Sheathed -> Melee -> Sheathed,
  each two ticks after its request; what the client shows is in "Interrupted".
- **The hotbar's weapon buttons** (`UseMeleeWeaponCommand`) draw the set by way of an animation the
  logic holds: `SpellAnimationLifetimeComponent` is created and the state changes; that component
  then keeps the weapon out after a later request to put it away, until it goes (about 3 s at speed
  1), and the change comes in that tick.
- **After a fight.** `CombatLeavingComponent` is created on the fighter in the tick the fight ends
  and is gone a few ticks later; it does not hold the weapon. With no attack under way the weapon
  goes away in the tick the fight ends. After a killing blow the attack's
  `SpellAnimationLifetimeComponent` holds it until it goes (78 ticks at speed 1, 9 at speed 8: the
  animation's game time), and the weapon goes away in the next tick. Osiris `CombatEnded` comes a
  tick or two after the component.

| Hypothesis (source) | Held? |
|---|---|
| a fight's end puts the weapon away (the brief) | yes, once nothing else holds it |
| the leaving component's timer (8 s, reading of the code) holds the weapon after a fight | no: it lives a few ticks and holds nothing seen here |
| drawing and sheathing is a state with a duration the logic waits for | no for the player's request: an instant change, the animation requested beside it; yes for an attack's or a weapon button's animation, which holds the state until it ends |
| quick toggles skip the animation (the owner) | the logic does both changes in full; the model stays on the back ("Interrupted") |
| the player's own request exists as a component (the brief) | yes: `esv::unsheath::UserRequestComponent`, made by the game from the key |

## The tests

- `tests/combat/weapon/after_fight.lua`: two cases, a fight ended by a scripted kill and by the
  host's fair killing blow; the weapon_state row against the leaving component's creation and the
  attack animation component's destruction.
- `tests/combat/weapon/toggle.lua`: the player's toggle (`GK.Act.Draw`, `Sheathe`) with the set in
  hand and with the other set (put in hand by the stage): the request component, the state two
  ticks later, the verb's answer equal to the row.

## What uses it

- `GK.Stage.Clean` leaves a weapon the game is putting away to the game and waits for its
  `StateChangedEvent` to `Sheathed`, drawing it again a tick later when the baseline had it drawn.
- `GK.Act.Draw` / `Sheathe` (UI side) press the bound key and answer on the client's replicated
  `eoc::unsheath::StateComponent` (`Ext.Entity.OnChange`); `GK.Cheat.Draw` / `Sheathe` are the
  server's order (`Osi.SetWeaponUnsheathed`). The client's `ecl::unsheath::VisualStateComponent`
  has no replication event (Script Extender refuses `OnChange` for it).

## Interrupted (`tests/combat/weapon/interrupted.lua`)

The client's `ecl::unsheath::VisualStateComponent` (`State`, `State2`), read every tick with the
prelude's `expect` (raw values: 3 is Sheathed, 1 Melee here), against the logic's rows.

- **A second toggle** 0.3 s of game time after the first (timed in the game, so as quick at any
  speed): the logic does both changes, two ticks after each request; the model never leaves
  Sheathed. The owner's observation holds, on the visual side: the weapon stays on the back.
- **A walk** right after the draw: the weapon stays drawn, logic and model.
- **The aiming of a jump or of an attack** right after the draw: `SpellAnimationLifetimeComponent`
  is created on the character (the targeting's animation) and holds the weapon out; a toggle back
  pressed with the aim open is carried out in the tick that component goes (about 3 s at speed 1),
  and the targeting is left.
- The model lags the logic: after a toggle back, the next case may begin with the model still
  drawn.
- **A second toggle within a server tick or two of the first is lost**: no
  `ServerUnsheathUserRequestPending` is made for it and the state changes once. Measured with two
  presses of the bound key a game timer apart (`GK.Act.Press`, the same chunk on the client): at
  speed 1, 40 ms apart lost, 80 ms and 150 ms apart both taken; at speed 8, 300 ms of game time
  (about one server tick) lost every time, at speeds 4, 3 and 2 taken 9 of 9, the two changes 2-5
  ticks apart. So the case's 0.3 s of game time holds up to speed 4; the test's ceiling is 4. Where
  the second press is dropped (the client's input handling or the server's request) is not read.

- **The toggle, then at once another action** (the bound key, then `GK.Gui.UseSlot("Projectile_Jump")`
  in the same client chunk, then the aim left), traced on the host on the server (`GK.Trace`, no
  component filter), at speed 1. Draw: `esv::unsheath::UserRequestComponent` in frame N, the
  jump's cast (`IsCasting`) and `DefaultChangedOneFrameComponent` in N+1, the state change
  (`StateChangedEventOneFrameComponent`, the state replicated) and the aim's
  `SpellAnimationLifetimeComponent` in N+2; the client's logical and visual states were both Ranged
  a moment later. Sheathe: the same frames, the state Sheathed in N+2. The aim did not wait for the
  draw, the draw was not cut short by the aim, and nothing on the server follows the animation: the
  state is data that changes two frames after the request; the drawing is the client's show
  (`ecl::unsheath::VisualStateComponent`).
- **`esv::unsheath::DefaultComponent`**: traced with the modifications and the systems tracked
  (entity: the host; component: `ServerUnsheath`), `esv::unsheath::DefaultSystem` writes it at every
  weapon request - the player's toggle (field_0 = 3, both ways), `Osi.SetWeaponUnsheathed(id, 1, 1)`
  (field_0 = 2), `(id, 0, 1)` (left at 2). field_0 is a `StatsUnsheathState` (0 Instrument, 1 Melee,
  2 Ranged, 3 Sheathed). It holds the game's record of the last request, so the stage keeps it as
  the game's (`Server/GameKeeps.json`, `any`). What reads it is not found: a read watch on the field
  loses it at once, the component's storage moving when the character's components change.

| Hypothesis (source) | Held? |
|---|---|
| the server's logic does not wait for the draw or sheathe animation; any action can follow at once (the owner) | yes: the state changes two frames after the request, an aim begun in the same tick neither waits nor cuts it; a test waits on the `weapon_state` row, never on the animation |
| quick toggles skip the animation, the weapon on the back at once (the owner) | yes, as what the model shows: the logic changes twice, the visual state stays Sheathed |
| a walk interrupts the drawing (the brief) | no: the weapon stays drawn |
| an aim begun while the weapon is on its way interrupts it (the brief) | no: the aim's animation component holds the weapon out until it goes |

## Open

- A cast begun and carried out (not cancelled) during the draw; the toggle in a fight, refused,
  shown by a test.
- What decides the visual skip (the draw animation's length against the interval): not measured.
- No code read, nothing named: the rule was settled with the game's components.
