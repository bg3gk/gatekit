# Rolls: what is still to do

The work on rolls, in order. An item leaves this file when it is done; what it found goes to
[studies/rolls.md](studies/rolls.md), and a bug stays `suspected` until it meets the bar in
[docs/writing-tests.md](../docs/writing-tests.md).

## The machinery

- Who asks for each die: every caller of the stream roll and of the dice loop named in the
  registry, each confirmed by a watch on a live roll.
- What bypasses the streams.
- Karmic Dice: where the debt is kept, how the forced die is picked, whether it overrides a pin on
  an attack and on a save, whether a load forgives it.
- One throw rigged: by the stream's count (shown on passive checks), then on an attack, a damage
  roll, a save; by the native interception, one roll among several in a tick.
- The roll log: a row per thrown roll with its origin, unknown origins marked.
- The judgement on Pin.
- The registry's roll entries with evidence that can confirm, under the game's own names.
- Rolls that arise by the game's own rules and were never watched, each a source for the question
  "who asks": an invisible creature against a character in sight who has See Invisibility
  (unverified: said to be one check, in a fight and perhaps outside one; followers of Bhaal are
  said to turn invisible); concentration and death saves; a status's ticking save; lock and trap
  windows; stealth; dispel; interrupts that adjust a roll; initiative.
- Calls from roll code that only show a result - the sound of a passive check, the sounds and the
  dice on the board in a roll window (dialogue, lock, trap) - told apart from the calls that
  decide the number: a watch says whether each moves a stream's count.

## The sweep of effects that use dice

Begins when the machinery above is covered. Every effect of the game that throws or changes a die
is gone through in turn, with the roll log on, and what the game computes is compared with what
it shows and with what the effect's text says.

How an effect is checked:

1. The list comes from the game's data, not from memory: every stats entry whose boosts,
   functors, `SpellRoll` or `OnApplyRoll` name a roll (`RollBonus`, `Reroll`, `MinimumRollResult`,
   `Advantage`, `Disadvantage`, `SavingThrow(`, `SkillCheck(`, damage dice, critical boosts,
   interrupts that adjust a roll), through `gatekit data`.
2. For each, three readings of one throw: the roll log's row (the dice, the kept die, the total,
   the difficulty, the origin), the combat log's line for it, the tooltip or description text.
3. A disagreement is one of: the log's terms do not sum to its total; the log names another
   difficulty, ability or source than the roll used; the roll is missing from the log; the
   description promises what the data or the code does not do. Each is recorded with which of the
   three readings is wrong.

The sweep is not blind. Reports are collected first and the most plausible are tried first:

- bg3.wiki's "Bugs" sections (there is no single list; they sit on each page) and its "Dice
  rolls" page;
- the wider search: Larian's forum, Reddit, Steam discussions, patch notes that name a fix to a
  roll or to the combat log, the changelogs of mods that fix rolls;
- the study mods that correct stats entries (their corrections name the entries they think
  wrong).

Leads at hand from the wiki, each to be reproduced:

| Lead | Page |
|---|---|
| A spell's save tooltip computes the difficulty with the ability saved against, not the caster's (Sacred Flame) | Dice rolls |
| The Perception roll that sets the difficulty of a Stealth check is not in the log; its natural die often equals the Stealth roll's | Stealth |
| The target's Halfling Luck or Reliable Talent also acts on that hidden Perception roll | Stealth |
| A shove's contested check shows the difficulty 0 | Shove |
| A shove's target with any advantage loses Acrobatics for the difficulty | Shove |
| Bane's save from Absolute's Bane is not in the log and offers no reaction | Gloves of Power |
| The Poisoned save of Infernal Weapon is not in the log | Infernal Weapon |
| Absolute Night: the log shows the spell's difficulty, the real one is fixed; Blinded is attributed to the attack | Vision of the Absolute |
| Water Whip: the log's difficulty uses Wisdom, the real one the higher of Dexterity and Strength | Water Whip |
| Hail of Thorns: the attack roll is not in the log, a second damage instance is | Hail of Thorns |
| Sentinel's snare: the damage roll is empty in the log | Sentinel |
| Karmic Dice ignores Reliable Talent when it computes the chance; a forced failure overrides it | Dice rolls |
| A dialogue that treats the speaker as source and target turns its own debt against it (Mirror of Loss) | Dice rolls |
| Items that give Stealth advantage when obscured always give it | Stealth |

Not yet looked for: reports outside the wiki; the combat log's own arithmetic (terms on the left
against the total on the right) needs a reader of the combat log's lines first.
