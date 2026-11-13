# Leads: findings not confirmed yet

Things read in code or seen once that still need a confirmation in the running game before
they may go to docs/curiosities.md or into a layout. Each says what would confirm it.

## The other unsheath timers

Read in code, not measured: summoned 3.0 s, coating 2.35, combining 1.5, lock picking 1.5
(joining and leaving a fight 8.0, resurrection 4.5 and consuming 0.6 are confirmed and on the
curiosities page). To confirm: capture `esv::unsheath::*Component` while a summon appears, a
weapon is coated, items are combined, a lock is picked.

## Countercharm instruments

Six `ARM_Countercharm_Instrument_*` stat entries use the otherwise unused instrument types.
To confirm whether they are reachable in play: look for templates and treasure tables that
use them.

## Layouts Script Extender already has that may be wrong

- `CombatCameraParams`: the use in `bg3.exe+0x1F21DB0` suggests the last three floats are
  ObstructionBufferTime, MinChangeCameraTime, MinAngle - a different order from the header.
- `TLLookAtAutomationConfig::EyeLookAtBone` is declared `uint8_t`; the code copies a
  FixedString at that offset.
- `eoc::PickingStateComponent` is declared as a tag; on the server its byte is 255 on all
  312 characters of the test level.
To confirm each: read the field through Script Extender and compare with the raw bytes and
with what the game shows (the raw stage of check.py works on mapped types too).

## One-byte types Script Extender itself may have the wrong way round

From the macOS build's default constructors (`macos_defaults.py`; the method agrees with the
live game on `eoc::PickingStateComponent`, whose byte is 255 on the server):

- declared as tags, but the constructor stores a byte (so they hold data):
  `eoc::CanTriggerRandomCastsComponent`, `eoc::PickingStateComponent`,
  `eoc::item::DestroyingComponent`, `eoc::lock::AnimationStateComponent`,
  `eoc::rest::LongRestInScriptPhase`, `eoc::rest::ShortRestComponent`,
  `esv::IsMarkedForDeletionComponent`, `esv::ScriptPropertyCanBePickpocketedComponent`,
  `esv::ScriptPropertyIsDroppedOnDeathComponent`, `esv::ScriptPropertyIsTradableComponent`;
- declared with a one-byte field, but the constructor writes nothing (empty structs):
  `eoc::ActiveComponent`, `eoc::GlobalLongRestDisabledComponent`,
  `eoc::GlobalShortRestDisabledComponent`, `eoc::StoryShortRestDisabledComponent`,
  `eoc::interrupt::ConditionallyDisabledComponent`, `eoc::interrupt::PreparedComponent`,
  `eoc::interrupt::ZoneSourceComponent`, `eoc::photo_mode::DummyShowSplatterComponent`,
  `esv::surface::LevelLoadedComponent`, `ls::SoundRoomCurrentStateComponent`.
To confirm each: read the byte on live holders on the server (non-zero settles "data");
for the second group, see whether the declared field ever differs from zero.
