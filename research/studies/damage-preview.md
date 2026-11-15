# Study: the "Damage Preview" mod, and why it can stall

Read offline with GateKit's databases, on game build 4.1.1.7631656: the mod's package as installed
(`DamagePreview_0e6b886d-c51f-75fb-f9bd-cf9c0c363c5b.pak`, by Caites & Noxis, depends on ImpUI).
Nothing was measured in the running game; what is established and what is a guess is kept apart.

## What the mod is

Established:

- **It contains no script.** The package is one interface page (`GUI/Pages/DamagePreview.xaml`),
  two state-machine files that swap the game's cursor-text widget for this page
  (`IUI_CursorText`, `ModType="Override"`), and metadata. No Lua, no stats, no Osiris.
- **The page is the game's own `CursorText.xaml` with additions.** The original is
  `Mods/MainUI/GUI/Pages/CursorText.xaml` in `Game.pak`. Compared binding by binding, the mod adds:
  - `CurrentPlayer.UIData.DamagePreviewWithResistances.Min` / `.Max` - the damage range;
  - `CurrentPlayer.UIData.ObservableDamagesPreview` (a list of `VMDamageRoll`) - the damage type icons;
  - `CurrentPlayer.UIData.ActiveTask.RootCastSpell.IsConcentrationSpell` and
    `SelectedCharacter.ConcentrationSpell.Icon` - the concentration warning.
- **The mod computes nothing.** Those properties belong to the engine's own view model
  (`gui::VMUIData::GetDamagePreviewWithResistances`, `GetFromUIObservableDamagesPreview` in the
  Toolkit's `NoesisGameUI.dll`; the property name is registered in `bg3.exe` at `+0x140DF30` and
  `+0x140FAA0`). The game fills them whether or not the mod is installed.
- **Larian built this display and hid it.** The game's `TargetInfo.xaml` has the same damage text
  and icon list bound to the same two properties, with the list collapsed and the triggers that
  would show it commented out under the note "Hidden as a test, see DamagePreview". The mod
  shows next to the cursor what the game prepared for the target panel.

## The producer

Read from the pseudo-code of `bg3.exe`; not profiled in the running game.

The number is computed by `gui::ViewModelProvider::OnUpdateOverheadTarget` -
`NoesisGameUI.dll+0x2D4880` in the Toolkit, **`bg3.exe+0x1330FC0`** in the game
(`bg3_dx11.exe+0x132FFB0`). The two are tied together by the "High Ground" / "Low Ground" text
handles that only this function uses, and by the fields it writes. The chain, with the Toolkit's
names:

1. `ecl::CharacterTask_Spell::SetCursor` calls `ecl::UIFeedbackManager::UpdateEntityTargeting`
   when the target under the cursor changes;
2. that raises a signal; the handler is `OnUpdateOverheadTarget(player, entity, TargetingInfo)`;
3. the handler builds an `eoc::HitDesc` and an `eoc::AttackDesc` on the stack, asks
   `ecl::StatsSystem` for a roll context (`LEGACY_CreateStatsRollContext`), then walks
   `UIData.AttackPreviewDamages` (the collection at `+0x818`) and for **every damage roll calls
   the function at `bg3.exe+0x1074F60` twice** - once with the minimum, once with the maximum. By
   its place in the call sequence this is `eoc::stats::ApplyResistance`, which in the Toolkit walks
   the target's boosts (`BoostsContainerComponent::ForEach`) and resolves stats expressions; the
   name is inferred, not proven;
4. the sums go to `UIData.DamagePreviewWithResistances` (`VMUIData +0x520`): `Min` at `+0x48`,
   `Max` at `+0x60` of the `VMMinMaxPair`, each followed by a property-changed notification.

What follows:

- The computation runs **synchronously in the UI handler, on every change of the hovered
  target**, and does not wait for the server. Its cost grows with the number of damage rolls and
  of boosts on the target.
- The handler has no check for whether anything is bound to the property, so the engine does this
  work with or without the mod. If that holds in the running game, the mod adds only the drawing,
  and its XAML cannot make the number arrive sooner.

Read it with `gatekit decomp code bg3.exe+0x1330FC0` and
`gatekit symbols callees NoesisGameUI.dll+0x2D4880`.

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
their data. This is a guess at where drawing time goes, from the markup alone. The damage text and
icon list sit inside the cursor panel, which moves with the cursor every frame and has drop
shadows; a value that arrives late makes the panel change size, which shows as a jump. With the
display hidden, as the game ships it, the same latency is invisible.

## What could be improved

In the markup alone (cheap, no native code):

- Bind the added elements through the data context already set on their container
  (`DataContext="{Binding CurrentPlayer.UIData}"` is two levels up) instead of searching up to
  the widget with `RelativeSource` for each value.
- Drop the extra drop shadow on the icon list, or share one effect on the parent panel.
- Give the damage text a fixed minimum width so that a value arriving late does not resize the
  panel.
- Hide the damage row while no value is there instead of showing an empty range.

These reduce drawing cost and visible jumping; they do not make the number arrive sooner. That
takes a native cache in front of `bg3.exe+0x1330FC0`, keyed by caster, spell and target and
dropped when boosts or stats of either change - with the usual cost in stability and in upkeep
after patches. The hook point's signature: `gatekit symbols signature bg3.exe+0x1330FC0`.

## What would settle it

A measurement in a running game: frame times while sweeping the cursor over targets with the mod
on and off, and with a single-roll attack against a many-roll spell. If frame time rises with the
mod off as well, the cost is the engine's computation; if only with the mod on, it is the page.
