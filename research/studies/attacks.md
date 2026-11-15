# Attacks

How a weapon attack is decided and carried out, and how a test makes one the player's way. Tests:
`tests/combat/melee/` (`attack_by_die.lua`, `scripted_attack.lua`), `ranged/`, `cast/`,
`unarmed/`, `offhand/`. The verb: `GK.Act.Attack` /
`Cast` (Client/Act.lua), the row: `game_hit` (Server/Act.lua); docs/contracts/lua-api.md, section 19.

## 1. The rule (bg3.wiki and wider)

- bg3.wiki "Attacks": an attack roll is d20 + ability modifier + proficiency against the target's
  armour class; finesse weapons take the higher of Strength and Dexterity.
- bg3.wiki "Damage": a roll at or above the armour class hits and rolls the weapon's damage dice
  (a dagger 1d4); below it misses and no damage is rolled.
- bg3.wiki "Dice rolls": a natural 1 on an attack roll always fails, a natural 20 always succeeds.
- bg3.wiki "Critical hit": a natural 20 is a critical hit; every damage die is rolled twice, flat
  modifiers are not doubled. The threshold can be lowered (Improved Critical and others).
- bg3.wiki "Resources": an attack spends the action; the action comes back at the start of the
  creature's turn, and a resource used outside combat recharges almost at once.
- No disagreement between the pages was found. Not read yet: patch notes on attack rolls, forum
  measurements of critical damage (a wish, not a gap in the rule).

## 2. The mods (study mods in the home, `gatekit mod usage`)

- Combat Extender reads `eoc::StatsComponent` and listens to Osiris `AttackedBy`; AI Allies writes
  `eoc::combat::ParticipantComponent.InitiativeRoll`; Sit This One Out listens to Osiris
  `CastedSpell`; Volition Cabinet's helper reads a caster's cast from
  `SpellCastIsCasting.Cast`. None reads the outcome of an attack roll: no mod among them
  subscribes to the spell-cast or hit event components. A mod made for attack logging or a combat
  log would be the next to read (none downloaded).

## 3. Script Extender

Declares what the instruments touched: `eoc::spell_cast::IsCastingComponent` (`Cast`),
`FinishedEventOneFrameComponent` (`Result`), `TargetHitEventOneFrameComponent` (`Hit`, `Attack`,
`Target`), `RollsComponent` (per target, `Casts[].Hit`), `esv::hit::HitResultEventOneFrameComponent`
(`Hit`, `Target`, `AC`, `Lethal`), `HitDesc.EffectFlags` (`DamageFlags`: Hit, Dodge, Miss,
Critical ...). Every field read here read as declared. Not checked: the damage dice under
`HitDesc.Damage.DamageRolls` (the read of them gave nothing; not pursued). Known, not ours: a traced
component change's `Flags` cannot be read (the property is declared and missing).
None found wrong among what was touched.

## 4. The instruments

The fork's tracer over one fair melee attack in a fight (server and client, ECB and immediate):

- Server, by ECS frame: the cast entity is created with its state when the client's request arrives
  (`esv::spell_cast::ClientInitiatedComponent`), the caster gets `IsCastingComponent`; then the
  animation request and the weapon drawn (`esv::unsheath::SpellAnimationLifetimeComponent`); then
  the logic runs; in one frame `CastEventOneFrameComponent`, `TargetHitEventOneFrameComponent`,
  the target's `HealthComponent` and `EntityDamagedEvent`; the next frame
  `esv::hit::HitResultEventOneFrameComponent` and the caster's `ActionResourcesComponent` (the
  action is spent when the hit resolves); about ten frames later `FinishedEventOneFrameComponent`
  with `OutcomeComponent`, then the cast entity's `DestroyEventOneFrameComponent`. Osiris
  `AttackedBy` (the `game_damage` row) comes with the hit, `CastedSpell` (`game_cast`) with the
  finish.
- Client: the caster's cast entity and `IsCastingComponent` exist from the start of the targeting
  (the preview hangs on it); its `FinishedEventOneFrameComponent` comes when the animation is over,
  seconds after the server's finish at speed 1. The cast's `RollsComponent` on the client holds the
  outcome per target (`EffectFlags`) before the animation shows it; the damage amount there is 0.

Hypotheses and whether they held:

| Hypothesis (source) | Held? |
|---|---|
| a miss leaves a trace of its own (the game shows "Miss") | yes: `HitResultEvent` is created for a miss too, flags `Dodge|Miss` (`Critical` on a natural 1) |
| the action is spent at the click (the hotbar greys it) | no: it is spent in the frame the hit resolves |
| the end of an attack is an event, not a state to poll (rule 3) | yes: `FinishedEventOneFrameComponent` on both sides; on the server Osiris `CastedSpell` too |
| Osiris casts skip the roll and the cost (the brief, from the rolls probe) | yes for a raw `Osi.UseSpell`; no for `GK.Cheat.Attack`, which takes the skipping options off the cast's state: it rolls and the game spends the action |

The hits measured (Astarion, dagger 1d4, Dexterity +3, a goblin of armour class 13; the cursor
shows 4-7 and 65%): die 1 a critical miss, 0 damage; die 19 a hit, every damage die pinned at 4:
7; die 20 a critical, the same pin: 11 (4 + 4 + 3). The same at speed 1 and 8.

## 5. The venue and the tests

Basic_Level_A, two hostile goblins (one never attacked, so the fight outlives the kill), the host
first by a pinned initiative. `attack_by_die.lua` (fair) and `scripted_attack.lua` (the contrast).
Both pass at speed 1 and at the working speed (`gatekit test tests/combat --speed 1`, then
`--speed working`).

## 6. Names and code

No function was named: the events found are the game's components, named by the game; no code
was read for this rung. The client-side end of a cast, the server's hit result and finished event
are enough for the tests and the verb.

## Findings

- finding: a fair melee attack's outcome follows the pinned die exactly as the wiki states:
  natural 1 a (critical) miss with no damage, 19 a hit with dice plus modifier, 20 a critical with
  the dice twice and the modifier once (`attack_by_die.lua`).
- finding: a miss writes neither damage nor an Osiris event; `esv::hit::HitResultEventOneFrame`
  is created for it, which is where the `game_hit` row comes from.
- finding: a raw Osiris attack hits at a pinned 1 and spends no action; `GK.Cheat.Attack` rolls and
  pays, because it strips the skipping options (`scripted_attack.lua`). The claim that the scripted
  attack always hits and is free holds for the raw Osiris cast only.
- finding: the client's end of a cast comes seconds after the server's (the animation); a fair verb
  that waits for the player's view must wait for the client.
- bug (ours, suspected - see section 7): reading the ECS from Ext.UI.Defer.
- todo: the critical threshold lowered (Improved Critical), advantage, resistance - the same rule
  with other inputs, each a test only when it is a rule of its own.

## 7. The known instability of the first aim

`tests/_selftest/clean/attack_in_fight.lua` failed once ("no character of this client is
controlled", while three copies ran tests). What its answer shows: the
check of `GK.Gui.Aim` ran `holds()` (which found the controlled character and its targeting on the
goblin) and, in the same call, `GK.Gui.Aiming()`, which found no controlled character - two
identical reads of the ECS disagreeing inside one Lua call. Script Extender runs an `Ext.UI.Defer`
callback on the render thread (`ScriptExtender::OnUiPredicatesManagerUpdate`, `PostUpdateNoesis`),
holding only the Lua lock; the main thread runs native ECS work until its next Lua call. The crash
of section 8 shows the main thread paused inside an ECS event dispatch while render-thread Lua ran.
Hypothesis, suspected: a read of the ECS from the deferred context can see the world mid-update.
Not reproduced: 36,723 reads in the deferred context over four fights, and 67 callbacks with a read
at the start and 3 ms later during a walk, saw no change; no `ClientControl` was created or
destroyed in a fight. What would settle it: the aim's ECS reads done on the main thread (in its own
`PickingHelper` post-update hook) and the deferred check reading that snapshot - a change of
`Client/Aim.lua`, the aiming executor's. Until then the failure is rare and not ours to work round.

## 8. Crash

The copy stopped once right after a fair attack ended: Script Extender's assertion
`global_ != -1` (LuaReference.inl), with the verb having called `Ext.Entity.Unsubscribe` from its
deferred check. The unsubscription changes the hook list `EntityComponentEventHooks` walks on the
main thread. The verb now subscribes once at load (main thread) and never unsubscribes; no crash
since.

## 9. Ranged weapons (`tests/combat/ranged/weapon_kinds.lua`)

The rule (bg3.wiki, "Attack roll", "Damage", "Weapons"): a ranged weapon attack adds the
Dexterity modifier to the roll and to the damage; the weapon gives its damage dice. The kinds by the
game's stats: `WPN_Shortbow` 1d6, `WPN_Longbow` 1d8, `WPN_HandCrossbow` 1d6, `WPN_LightCrossbow`
1d8, `WPN_HeavyCrossbow` 1d10 (`Ext.Stats.Get(...).Damage`). The attack is the hotbar's
`Projectile_MainHandAttack`, through `GK.Act.Attack(target, { spell = ... })` - the verb's ranged
case. Each weapon is made from a root template whose `Stats` is the kind and equipped in the ranged
slot by script (a cheat); the die pinned at 19, every damage die at its largest face.

| Hypothesis (source) | Held? |
|---|---|
| every kind hits for its dice plus one and the same modifier (the wiki) | yes: 9, 11, 9, 11, 13 = the dice at most + 3, the cursor's maximum each time, at both speeds |
| the ranged attack goes through the same fair verb as melee (api-rules, rule 1) | yes: GK.Act.Attack with the ranged spell |

Found on the way: the stage's party baseline holds no weapon slot. `PARTY_SLOTS` (Server/Stage.lua)
names them `MeleeMainHand`, `RangedMainHand` ..., but `Osi.GetEquippedItem` knows them as
`Melee Main Weapon`, `Ranged Main Weapon`, so Clean never puts a weapon back. After the first run
the ranged slot was empty and Verify named only one boost fewer. With the slot names corrected
the clean equips the baseline's weapon again by itself, and the test leaves it to the clean.

## 10. A spell's saving throw (`tests/combat/cast/saving_throw.lua`)

The rule: bg3.wiki "Saving throws" (d20 + the save's ability modifier + proficiency against the
caster's spell save DC; a success reaches the DC), "Dice rolls", Natural 1s and 20s (a spell's save
is not decided by a natural 1 or 20; only concentration saves and a dialogue's are), "Sacred Flame"
(a Dexterity save, 1d8 radiant on a failure, nothing on a success). The wiki also records a tooltip
bug: a spell's tooltip computes its DC with the caster's modifier of the *saved* ability; not
measured here (the test reads the DC the hit used, not the tooltip).

Where the save lives: the target's roll is resolved inside the hit, in `HitDesc.ConditionRolls`
(`ConditionRoll`: `RollType` SavingThrowRoll, `Ability`, `Difficulty`, `Roll.Result` with
`NaturalRoll`, `Total`, `Critical`). No `esv::roll` finished event names it. The verdict is the
hit's: `EffectFlags` holds `SavingThrow` on a hit the target saved against; a failed save is an
ordinary hit (`Hit`, `HitpointsDamaged`). `game_hit` rows now carry `saves`. Tried on two unlike
cases: Sacred Flame by the fair verb (one target) and Acid Splash by `GK.Cheat.Cast` (two targets,
one saving, one failing: a save per target in its own row). A pin of kind `SavingThrow` on the
target binds the spell's save.

| Hypothesis (source) | Held? |
|---|---|
| the total decides, not the die (the wiki) | yes: DC 11, a 1 (total 2) fails and a 20 (21) saves; DC raised to 26, a 20 fails; DC lowered to -4, a 1 saves; at both speeds |
| a natural 20 marks the roll a critical success (the dice) | the roll's `Critical` is Success for every natural 20, also the one that failed: it is not the verdict |
| Osiris `RollResult` reports every save (the rolls executor's `game_roll` rows stand on it) | no: it came for every saved spell save and for none of the failed ones (four of each read from the log: three by the verb, one by script); and its row reads "passive check", without kind, DC or die, because no finished roll is there to join |

## 11. The unarmed strike (`tests/combat/unarmed/strike.lua`)

The rule: bg3.wiki "Unarmed Strike": an action of a creature with no melee weapon equipped; a
melee unarmed attack roll; damage 1 + the Strength modifier, bludgeoning. The game's own formula:
`Target_UnarmedAttack`, `Attack(AttackType.MeleeUnarmedAttack)`, `DealDamage(UnarmedDamage,
Bludgeoning)`. The spell is the game's (source `Guaranteed`) while the main hand is empty.

| Hypothesis (source) | Held? |
|---|---|
| 1 + the Strength modifier (the wiki) | for a modifier of 0 or more: Strength 14 deals 3. Below that the game deals 1: Strength 8 (-1) and 2 (-4) both 1, the cursor 1-1 |
| a critical doubles dice and not flat bonuses (the wiki, "Critical hit") | yes: a critical deals 3 as the hit did |
| the roll adds Strength (the wiki) | yes: the cursor's hit chance 30, 45, 60 % for -4, -1, +2: 5 % a point |

Not settled: whether the 1 is a floor of the damage or a negative modifier left out (a flat strike
cannot tell them apart; a damage bonus added would).

**Bug (suspected, the shared stage's): Clean adds a copy of a weapon's attack.** `RestoreMember`
puts the party's spells back before their equipment, and `Osi.Equip` / `Unequip` land a few ticks
after the call (shown: the slot and the spellbook unchanged in the same chunk, changed by the next).
After a test that took the main-hand weapon off, Clean sees `Target_MainHandAttack` lost and adds
it with `Osi.AddSpell` (source `Osiris`), then equips the weapon, which brings its own
(`WeaponSpell`). The copy stays; Verify compares spells by name and calls the scene clean; while
the copy is there the game grants no unarmed strike (shown: removing the copy brought it back at
once). A `Projectile_MainHandAttack` copy was found the same way. Removing a copy by hand took the
weapon's hotbar slot with it (the slot was bound to the copy); unequipping and equipping the bow
again put it back. Proposed: in RestoreMember equip first and diff the spells only once the
equipment is as it was, or leave the spells an item grants (`WeaponSpell`, and the `Guaranteed`
unarmed strike) out of the diff. Until then the unarmed test equips the weapon again itself.

## 12. The off-hand attack (`tests/combat/offhand/light_weapons.lua`)

The rule: bg3.wiki "Light (weapon property)": with a light weapon in each hand, after the main-hand
attack, a bonus action makes an off-hand attack; by default no ability modifier is added to its
damage; the hotbar's Dual Wielding toggle decides whether the two attacks are made together.
"Two-Weapon Fighting": the style adds the modifier. A second light weapon equipped by `Osi.Equip`
goes to the off hand when it is empty, and grants `Target_OffhandAttack` (`WeaponSpell`).

The toggle is `eoc::DualWieldingComponent` (`MeleeToggledOn`, `RangedToggledOn`, `MeleeUI` ...),
the character's own state (found on in one game process, off in a level started cold); equipping
an off-hand weapon turns `MeleeToggledOn` on. Written on the server and replicated, the game
follows it.

| Hypothesis (source) | Held? |
|---|---|
| no ability modifier on the off-hand damage (the wiki) | for a positive one: the off-hand dagger (Dexterity +3) deals 4 = its die. A negative one is kept: the off-hand handaxe (Strength -1) deals 5 = 6 - 1, as the tabletop rule says and the wiki does not |
| the off-hand attack is the bonus action's | yes: action 0, bonus 1 after the main hand; bonus 0 after the off hand |
| with the toggle on, one attack makes both (the wiki) | yes: the main-hand attack makes two hits, 7 and 3, both named `Target_MainHandAttack` in the hit, and spends the action and the bonus action; `Target_OffhandAttack` is then refused for want of the resource |

The toggle the player's way is not reached yet: the hotbar's `DualWieldToggle` is an
`ls.LSToggleButton` whose data context is an `ls.VMInputEvent`; `GK.Gui.Click` runs the XAML chain
and finds no `ToggleDualWieldingCommand` on that context; running the button's own `Command` by
hand did toggle it. The input event `ToggleDualWielding` is bound to RETURN in the profile and the
pressed key changed nothing. A wish for `GK.Gui.Click` (not mine): fall back to the control's own
command when the chain names none. Not a case yet: the off-hand attack before any main-hand attack
(the wiki says it comes after one).

## 13. The attack roll in the machine code

Hypothesis (the wiki's "Attack roll": the d20 and the bonus against the armour class, a natural 1
always misses, a natural 20 always hits): held, and located. A weapon attack's d20 is drawn twice on
the attacker's PlayerSpell roll stream (the pair) by `esv::roll::stream::RollOnStream`
(+0x105B800) through `RollDiceWithBoosts`, which bounds each die by the pin's boosts, adds the bonus
and fills the roll result; `RollAttackAgainstArmourClass` (+0x106CA40) takes the armour class from
`eoc::ResistancesComponent.AC`, has `DecideAttackHit` (+0x1067D50) judge it (natural 1 a miss, a
total below the AC a miss unless a natural 20), writes the hit's `EffectFlags` and appends the roll
with the AC to the hit's `ConditionRolls`. So the natural die of an attack is readable in the hit,
as a save's is, and `game_hit.attack` now carries it. The damage dice are drawn on the PlayerDamage
stream by `RollDamageDice` (+0x1070DE0). Evidence: a write watch on the streams' counts, a freeze
of the target's AC (21 turned a 20 into a miss), the hit read through Script Extender; details in
research/proof-of-concept/reports/attacks.md, "The descent, a fourth run".
