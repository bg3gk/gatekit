# Study: the "Damage Preview" mod, and why it can stall

Looked at offline on 2026-11-12 with the GateKit databases: the mod's package as installed
(`DamagePreview_0e6b886d-c51f-75fb-f9bd-cf9c0c363c5b.pak`, 152 KB, by Caites & Noxis, depends on
ImpUI), game 4.1.1.7631656. Nothing was measured in the running game; what is established and
what is a guess is kept apart.

## What the mod is

Established:

- **It contains no script.** The package is one interface page (`GUI/Pages/DamagePreview.xaml`,
  803 lines), two state-machine files of 13 lines that swap the game's cursor-text widget for
  this page (`IUI_CursorText`, `ModType="Override"`), and metadata. No Lua, no stats, no Osiris.
- **The page is the game's own `CursorText.xaml` with additions.** The original is
  `Mods/MainUI/GUI/Pages/CursorText.xaml` in `Game.pak` (605 lines). Compared binding by binding,
  the mod adds:
  - `CurrentPlayer.UIData.DamagePreviewWithResistances.Min` / `.Max` — the damage range;
  - `CurrentPlayer.UIData.ObservableDamagesPreview` (a list of `VMDamageRoll`) — the damage type icons;
  - `CurrentPlayer.UIData.ActiveTask.RootCastSpell.IsConcentrationSpell` and
    `SelectedCharacter.ConcentrationSpell.Icon` — the concentration warning.
- **The mod computes nothing.** Those properties belong to the engine's own view model
  (`gui::VMUIData::GetDamagePreviewWithResistances`, `GetFromUIObservableDamagesPreview` in the
  Toolkit's `NoesisGameUI.dll`; the property name is registered in `bg3.exe` at `+0x140DF30` and
  `+0x140FAA0`). The game fills them whether or not the mod is installed.
- **Larian built this display and hid it.** The game's `TargetInfo.xaml` has the same damage text
  and icon list bound to the same two properties, with the list collapsed and the triggers that
  would show it commented out under the note "Hidden as a test, see DamagePreview". The mod
  shows next to the cursor what the game prepared for the target panel.

So when the number is late or the game hitches, the time is spent in the engine code that fills
`DamagePreviewWithResistances`, or in drawing the page — not in anything the mod calculates.

## What the page costs to draw

Counted in the two files:

| | Game's `CursorText.xaml` | Mod's page |
|---|---|---|
| Bindings | 129 | 137 |
| Multi-bindings | 12 | 14 |
| Bindings that search up the tree (`RelativeSource`) | 12 | 22 |
| Data triggers | 112 | 116 |
| Lists (`ItemsControl`) | 12 | 18 |
| Drop-shadow effects | 1 | 2 |
| Stroked text styles | 2 | 6 |

The mod's page is a little heavier than the original, not a different order of size. The
additions that cost something each frame they change are the second drop shadow (on the icon
list), four more stroked text styles, and ten more bindings that walk up to the widget to find
their data. This is a guess at where drawing time goes, from the markup alone.

## Where the stalls most likely come from — hypotheses

1. **The engine recomputes the preview when the hovered target changes.** A damage range with
   resistances needs the attacker's and target's stats, boosts and conditions evaluated for the
   selected spell. For spells with many damage rolls or conditional functors that is real work,
   done when the cursor moves onto a new target. Larian hiding the display "as a test" is
   consistent with the feature not being finished to their standard, performance included.
2. **Every change of the value re-lays out the widget.** The damage text and icon list sit
   inside the cursor panel, which is translated every frame to the cursor position and has
   drop shadows; a value arriving late makes the panel change size, which is visible as a jump.
3. **The mod widens when the engine has to care.** With the display hidden, a slow or stale
   value is invisible. Shown at the cursor, the same latency is felt.

I could not find, offline, which function fills the property: its setter in the Toolkit build
(`gui::VMUIData::SetDamagePreviewWithResistances`) has no direct callers and no other DLL imports
it, so it is reached through the interface's property system. The client-side system for spell
previews is `ecl::spell_preview::PreviewSystem`; the client-server messages
`eocnet::spell_cast::StartPreviewMessageData` and `UpdatePreviewMessageData` exist, which leaves
open that part of the preview waits on the server. That would explain "takes long sometimes" in
multiplayer or under server load, and is the first thing to check.

## What could be improved, in theory

In the markup alone (cheap, no native code):

- Bind the added elements through the data context that is already set on their container
  (`DataContext="{Binding CurrentPlayer.UIData}"` is two levels up) instead of searching up to
  the widget with `RelativeSource` for each value.
- Drop the extra drop shadow on the icon list, or share one effect on the parent panel.
- Give the damage text a fixed minimum width so that a value arriving late does not resize the
  panel.
- Hide the damage row while no value is there instead of showing an empty range.

These reduce drawing cost and visible jumping. They do not make the number arrive sooner.

To make the number arrive sooner the computation has to change, and that is engine code:

- find the function that fills `DamagePreviewWithResistances` (pseudo-code search for the users
  of the property, once `bg3.exe` is decompiled; then a profile in the running game);
- if it recomputes on every cursor move, cache per (caster, spell, target) and invalidate on
  stat change — a native mod, with the usual cost in stability and in upkeep after patches;
- if it waits for the server, there is little to gain on the client side.

## What would settle it

A measurement in the running game with the test stand: frame times while sweeping the cursor
over targets with the mod on and off, and with a single-roll attack against a many-roll spell.
If frame time rises with the mod off as well, the cost is the engine's computation; if only with
the mod on, it is the page.

## The producer, found in the pseudo-code (2026-11-12)

Read offline from the decompiled `bg3.exe` 4.1.1.7631656; not profiled in the running game.

The number is computed by `gui::ViewModelProvider::OnUpdateOverheadTarget` —
`NoesisGameUI.dll+0x2D4880` in the Toolkit, **`bg3.exe+0x1330FC0`** in the game
(`bg3_dx11.exe+0x132FFB0`). The two were tied together by the "High Ground" / "Low Ground" text
handles that only this function uses, and by the fields it writes.

The chain, with the Toolkit names:

1. `ecl::CharacterTask_Spell::SetCursor` calls `ecl::UIFeedbackManager::UpdateEntityTargeting`
   when the target under the cursor changes;
2. that raises a signal; the handler is `OnUpdateOverheadTarget(player, entity, TargetingInfo)`;
3. the handler builds an `eoc::HitDesc` and an `eoc::AttackDesc` on the stack, asks
   `ecl::StatsSystem` for a roll context (`LEGACY_CreateStatsRollContext`), then walks
   `UIData.AttackPreviewDamages` (the collection at `+0x818`) and for **every damage roll calls
   the 543-line function `bg3.exe+0x1074F60` twice** — once with the minimum, once with the
   maximum. By its place in the call sequence this is `eoc::stats::ApplyResistance`, which in the
   Toolkit walks the target's boosts (`BoostsContainerComponent::ForEach`) and resolves stats
   expressions; the name is inferred, not proven;
4. the sums go to `UIData.DamagePreviewWithResistances` (`VMUIData +0x520`): `Min` at `+0x48`,
   `Max` at `+0x60` of the `VMMinMaxPair`, each followed by a property-changed notification.

What follows for the mod:

- The computation runs **synchronously in the UI handler, on every change of the hovered
  target**, and it does not wait for the server. The first two hypotheses above (engine cost,
  cost growing with the number of rolls and of boosts on the target) fit; "waiting for the
  server" does not.
- The handler has no check for whether anything is bound to the property, so the engine does this
  work with or without the mod. If that holds in the running game, the mod adds only the drawing,
  and its XAML cannot make the number arrive sooner. Whether the page of the mod adds a visible
  cost of its own is still the measurement described below.
- A native speed-up would be a cache in front of `bg3.exe+0x1330FC0` keyed by caster, spell and
  target, dropped when boosts or stats of either change. The hook point and its signature:
  `python symbols\symbols.py signature bg3.exe+0x1330FC0`.

Commands: `python symbols\decomp\decomp.py code bg3.exe+0x1330FC0`,
`python symbols\symbols.py callees NoesisGameUI.dll+0x2D4880`.
