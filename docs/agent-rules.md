# Baldur's Gate 3 rules an agent should know (where the game differs from D&D 5e)

A player who knows Dungeons & Dragons 5th edition (2014) knows most of BG3. These are the
differences that change decisions in play - mechanics only, nothing about places, people or the
story. Summarised in our own words from bg3.wiki's page "D&D 5e rule changes" (community wiki,
CC BY-NC-SA 4.0); facts checked against the game where GateKit reads them. `the honest-play API
rules` prints this file, so an agent has it without reaching any website.

## Turns and actions

- **Initiative** is a d4 plus bonuses, not a d20 Dexterity check. Party members next to each
  other in the turn order **share a turn**: each acts in any order, even interleaved, and the group
  ends when every member has ended (GK: `combat` lists all open turns; `do endturn all`).
- **Jump** is a bonus action that costs 3 m (10 ft) of movement whatever the distance: about 4.5 m
  (15 ft) at Strength 10 or less, +0.9 m per 2 points above. It also crosses gaps and reaches
  ledges walking cannot. **Shove** is a bonus action for everyone; it pushes (off ledges too) but
  does not knock prone by itself.
- **Drinking a potion is a bonus action.** Throwing a potion (an action) breaks it on a small area.
- **Help** brings a downed ally back to 1 HP (or ends some conditions such as being webbed or
  asleep); it does not give advantage. There is no Dodge, no Ready and no grappling.
- Switching between the melee and the ranged weapon set is free and unlimited. Equipping a weapon
  from the inventory costs an action; donning or doffing armour costs an action.
- The reaction comes back at the **end** of the character's turn.
- Off-hand bonus attacks do not need the Attack action first.
- **Weapon actions** (special attacks per weapon type: Slash, Pommel Strike, Piercing Thrust...)
  need proficiency and usually recharge on a short rest.
- **Dip**: a weapon dipped (bonus action) in fire, acid or poison adds that damage for a few turns.

## Attacks, positions and vision

- **High ground**: a ranged attack from 3 m (10 ft) or more above the target gets +2; from that much
  below, -2. Cover does not exist, but ranged attacks, throws and projectile spells need a line of sight.
- **Threatened**: ranged attacks within 3 m (10 ft) of a hostile with a melee weapon have
  disadvantage. Opportunity attacks happen only from a creature holding a melee weapon.
- Attacks against a **prone** creature have advantage within 3 m (10 ft); a prone creature spends
  half its movement to stand up. **Paralysed** targets within 3 m are hit critically.
- Ranges are short: most spells and normal ranged weapons reach 18 m (60 ft); darkvision is 12 m.
- **Stealth**: creatures notice only what they see (no hearing). Hiding outside every sight cone
  succeeds; inside one, the hider's Stealth is rolled against the watcher's passive Perception
  (GK: `watchers`, `exposure`).
- Spells can target creatures the caster cannot see; Obscured creatures can be attacked up close.

## Spells

- Components are ignored (only Silence stops most spells); no focus is needed.
- Prepared casters change prepared spells at any time out of combat. Rituals cost no extra time
  but cannot be cast in a fight.
- **Anyone can read any spell scroll**, using their own spellcasting ability.
- A levelled spell can be cast with the action and another with the bonus action in one turn.
- Concentration works as in 5e; fleeing a fight ends it.

## Damage, death and rest

- Party members at 0 HP are **downed** (death saving throws), never killed outright by damage.
  Most other characters die at 0 HP and stay dead.
- Death saves: a 1 counts as one failure, a 20 as a success (no free hit point).
- Falling damage is a share of maximum HP; a fall that takes more than a quarter knocks prone.
- **Short rests**: two per long rest; each restores half the maximum HP (no hit dice).
- **Long rests** take camp supplies (food); too few supplies give only part of the recovery.
- New condition **Wet**: no burning, resistance to fire, vulnerable to lightning and cold.
- **Frightened** creatures cannot move at all.

## Checks and dice

- A natural 1 on an ability check always fails and a 20 always succeeds; in dialogue this also
  holds for saving throws. Ordinary saving throws have no automatic results.
- **Inspiration** (up to four for the party) rerolls a failed check or save in dialogue.
- The default "Karmic Dice" setting smooths out streaks of bad rolls.
- Contested checks are rolled against the opponent's passive skill.
- There are no tool proficiencies: lockpicking and disarming traps use Sleight of Hand with
  Thieves' Tools / a Trap Disarm Toolkit, which can break on a failure.

## Carrying, equipment, levels

- Carrying capacity: 40 kg + 10 kg per Strength point; above 80 % encumbered, above 93 % heavily
  encumbered (no jumping or climbing).
- No attunement; two ring slots; regular arrows are unlimited (only special ones are counted).
- Level cap 12; hit points per level are fixed; multiclassing has no ability requirements.

## Leaving a fight

- A character far enough from every enemy can **flee** the fight: it leaves the fight (and ends its
  concentration) while the others go on. GK: `combat` says who can flee; `do flee`.
