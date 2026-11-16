# Study: Surprised

When a creature of Baldur's Gate 3 becomes Surprised: the necessary and sufficient conditions, read
from the game's code, from its data and from staged scenes. The tests are under
[tests/surprise](../../tests/surprise); what the status does once held is also in
`test:fight/npc/surprised`. Addresses are of `bg3_dx11.exe`, build 25605617.

## 1. The verdict

The status `SURPRISED` has two givers and no third: the engine's own rule, and the story's scripts.

**The engine's rule.** A creature T receives `SURPRISED` for one round from the engine exactly when
one of A or B holds, and C does not prevent it:

- **A - the target of a hit from hiding.** All of:
  1. T is a character (not an item) and joins a fight in this tick, having been in none
     (`code:esv::combat::System::CheckForSurprise` is called from the start of a fight alone, for
     each side of a pair of which at least one was in no fight; `test:surprise/already_fighting`).
  2. At that tick a hit on T exists whose attack flags hold `FromSneak`
     (`test:surprise/hit_before_the_fight`: a hit that lands a tick after the joining gives nothing,
     though it carries the flag). The flag is on the hits of an attacker who holds the status
     `SNEAKING` or `INVISIBLE` as the attack is made, and not on those of an attacker who holds
     neither, seen or not (observed: section 5, rows 1, 4, 5, 12).
  3. The hit names a spell, and the spell's flags do not hold `NoSurprise` (read only: no spell of
     the game's data has the flag, `gatekit data sql` over `stat_fields`; a hit with no spell - a
     surface, a status's damage - is skipped by the same read).
  4. The hit's attacker is not T's ally: the relation of the two is not 100 (read only: every
     staged attacker was hostile).
- **B - an ally of somebody surprised.** One of:
  1. T is a participant of the fight of a creature that satisfies A in this tick, and T's relation
     to that creature is 100 (`code:esv::combat::SurprisedSystem::SurpriseAlliesOfStealthAttackTargets`;
     `test:surprise/from_hiding`: the ally beside the target).
  2. T joins a fight in this tick, and a member of T's party, or a member of T's combat group whose
     relation to T is 100, is surprised - holds an active `SURPRISED` from any giver, or satisfies A
     in this tick (`code:esv::combat::SurprisedSystem::SurpriseEntitiesJoiningCombat`; the combat
     group observed, section 5 row 15; the party read only).
- **C - immunity.** T holds a boost `StatusImmunity(SURPRISED)` or its stats entry lists the status
  in `PersonalStatusImmunities` (`test:surprise/alert`). The immunity is T's own: the allies of an
  immune target are surprised as before.

Nothing else enters the rule. It does not ask whether the attack hits (a miss surprises:
`test:surprise/from_hiding`, case "the arrow misses"), whether it is melee, ranged or a spell, with
or without an attack roll (section 5, rows 1-3, 6-9), which side the attacker is on (a hidden enemy
surprises the party the same way, row 13; an NPC surprises an NPC with no player near, row 14), nor
the target's own perception as such: sight decides only whether the fight begins before the hit.

**Why the same opener sometimes fails.** Condition A.2 is a race between the hit and the start of
the fight. A hidden attacker keeps `SNEAKING` until its attack lands (the status's own
`RemoveConditions`: `OnAttack`), so the hit comes first and the fight follows two ticks later, by
the hit: hiding surprises with every opener tried. An invisible attacker loses `INVISIBLE` as the
spell is cast (`OnSpellCast`); the target then sees it and the fight begins by sight in the same
tick. A blade's hit is already there; an arrow's is not while it flies. A thrown object makes its
first hit, the one that carries `FromSneak`, on the object itself, and the hits on the creature come
seventeen ticks after the fight began with no such flag (row 11).

**The story's scripts.** Every other Surprised is a script's `ApplyStatus`; the list is section 3.
A script-given status counts for B.2 like the engine's own.

## 2. The claims

Sources: bg3.wiki, the pages "Surprised (condition)", "Turn-based mode", "Combat", "Initiative",
"Alert", "Feral Instinct", "Elixir of Vigilance", "Assassinate: Ambush", "Assassin"
(`gatekit wiki page`). The marks are section 7.

| # | Claim | Page |
|---|---|---|
| 1 | A surprised creature cannot take actions or reactions for one combat round | Surprised |
| 2 | The status loses its duration at the end of the round | Surprised |
| 3 | A creature attacked while unaware of its attacker at the start of combat - such as when the attacker is hidden - is surprised | Turn-based mode, Combat |
| 4 | The first round is then a surprise round in which only the attackers act | Combat |
| 5 | Alert, Feral Instinct and the Elixir of Vigilance prevent being surprised | Surprised, Alert, Initiative |
| 6 | Some encounters are set to surprise the party or the enemy whatever is done | Surprised, Turn-based mode |
| 7 | The key is line of sight: the enemies must have a clear line of sight to the character surprising them | Surprised |
| 8 | Melee attacks always work | Surprised |
| 9 | Ranged attacks made at melee range always work | Surprised |
| 10 | Spell attacks work nearly always at melee range and less often beyond it | Surprised |
| 11 | Eldritch Blast works at both ranges, Fireball and Witch Bolt at melee range only, Fear at both, Magic Missile at neither | Surprised |
| 12 | Where an attack type works, a miss works as a hit does | Surprised |
| 13 | Grenades do not work, even on a direct hit | Surprised |
| 14 | The most reliable way to surprise is to become invisible and attack, from any source of invisibility | Surprised |
| 15 | Some surprised creatures can warn nearby allies, keeping them from being surprised too | Turn-based mode, note 4 |
| 16 | Any successful attack roll against a surprised creature is a critical hit for an Assassin | Assassinate: Ambush |
| 17 | A participant that passes its turn in a surprise round stays ahead of late joiners in the initiative order | Initiative |

The tabletop rule it descends from (5e, "Surprise") is decided by a Stealth check against passive
Perception for every creature apart, and the surprised keep their turn but cannot act or move in it.
The game decides it for a whole side by one hit, as below.

## 3. The givers and the immunities, from the data

Searched: every story goal, behaviour script and stats file of the index whose text holds
"urpris" in any case (`gatekit data sql` over `texts` joined to `texts_fts_content`), every stats
field whose value holds `SURPRISED`, `Surprise` or `NoSurprise` (`stat_fields`), and every dialogue
that sets the flag below (`dialog_flags`). The search is of the index's texts: a goal the index does
not hold is not covered.

**No functor gives it.** No spell, status, passive, interrupt or item of the stats names `SURPRISED`
in an `ApplyStatus`. The status's entry (`gatekit data stat SURPRISED`): four
`ActionResourceBlock` boosts (action, bonus action, reaction, movement), `TickType` `EndRound`,
`StackId` `SURPRISED`.

**Scripts that apply it.** Three texts call `ApplyStatus` with the status:

| Where | Condition, as the text states it |
|---|---|
| `_Greevers_Little_Helpers`, `PROC_MakeSurprised(object [, turns, forced])` | applies it for `turns` x 6 seconds (default one turn, not forced) unless the object already holds a longer or a permanent one; `turns` -1 is permanent |
| `Act1b_CRE_Brewery` | on a kobold, 6 seconds, forced, as its scene turns to a fight |
| `DEN_BugbearNinja.ann` (behaviour script) | on the bugbear itself, 6 seconds, once, when its ambush on Nadira is broken |

**Callers of `PROC_MakeSurprised`**, each a scripted moment of one encounter:
`_Greevers_Little_Helpers` (the object flag `GLO_Event_SetSurprisedNextCombat`, set by the dialogues
`PLA_DyingHyena_DeathEvent`, `UND_LoneDuergar_Henchmen_Surprised`,
`HAV_EnteringHaven_TadpoleCheckpoint`, `MOO_Executioner`); `_Global_Ambush` (below);
`Act1_CHA_Chapel`, `Act1_GOB_WolfPens`, `Act1_PLA_DyingHyenas`, `Act2_HAV_TakingIsobel_FlamingSpy`,
`Act2_HAV_TakingIsobel_InnAttack`, `Act2_SCL_Misc`, `Act3_WYR_Danthelon_Jah`,
`Act3_WYR_SentientAmulet`, `Act3_WYR_Windmill`, `Act3b_LOW_HouseOfHope2`,
`Act3b_LOW_MindflayerOutburst`, `Act3b_LOW_StormshoreTabernacle`, `GLO_Mimic`.

**The story's ambush** (`_Global_Ambush`): enemies wait with the status `AMBUSHING` (and
`INVISIBLE` where the ambush manages it), unable to join a fight, around a trigger. A party member
who steps into the trigger while not hidden and not in a fight launches the ambush, and
`PROC_Ambush_SetPlayersSurprised` surprises every party member within 15 m of that one - unless the
party passed the ambush's Perception check (`DB_AmbushSurpriseImmune`), an ambusher is itself
surprised, or the ambush was revealed. Violence against an ambusher before that surprises every
ambusher of the site instead. The same procedure is called by `Act1_FOR_ThayanCellar`,
`Act1_HAG_ForestIllusion`, `Act1_UND_PetrifiedDrow` and `Act2_SCL_Misc`.

**Immunities.** `StatusImmunity(SURPRISED)` is a boost of the passives `Alert`, `FeralInstinct` and
`Surprise_Immunity` and of the statuses `ALCH_ELIXIR_ALERTNESS` and
`LOW_PHILGRAVESMANSION_CANOPICAURA_BRAIN`; `Surprise_Immunity` is in the stats of the blights and
the shambling mound of the shadow-cursed lands, the steel watchers, the githyanki captain and
inquisitor, Orin, Sarevok and Oliver, comes with the status `GITHYANKI_PRODIGY_NORMAL`, and is added
and removed by `Act2_TWN_Tollhouse`. `LOW_BhaalTemple_RitualHelper` lists the status in
`PersonalStatusImmunities`. No status or script removes `SURPRISED` early.

**What keys on it.** `Assassinate_Ambush`: `IF(HasStatus('SURPRISED')):CriticalDamageOnHit()`. The
tutorial `Surprised` is shown by `GLO_Tutorials` when a player's enemy holds the status.

### What the mods do

No study mod applies `SURPRISED` by a functor or a script of its own, sets the spell flag
`NoSurprise`, or reads the two request components (a search of their files); three give
`StatusImmunity(SURPRISED)` on items or passives (Ancient Mega Pack, Justice for Nessa, Fantastical
Multiverse). Script Extender's enumeration of spell flags lists `NoSurprise` as bit 24, the bit the
code tests.

## 4. The engine's path, from the code

| Step | Code | Rung |
|---|---|---|
| A pair of creatures of which at least one is in no fight starts or joins a fight; for each side that joins, the check below runs | the start of a fight inside the combat system's update | read |
| The check: a hit on the joiner with `FromSneak` (bit 4 of the attack flags of `eoc::hit::TargetComponent`), a spell without `NoSurprise`, an attacker whose relation is not 100; the joiner is given `esv::combat::SurprisedStealthRequestOneFrameComponent` | `code:esv::combat::System::CheckForSurprise` | confirmed: 3 calls for 3 joiners, the request on the target alone |
| Each tick: joiners with a surprised party or combat-group ally, then the allies of each stealth target in its fight, are given `esv::combat::SurprisedJoinRequestOneFrameComponent` | `code:esv::combat::SurprisedSystem::Update`, `code:esv::combat::SurprisedSystem::SurpriseEntitiesJoiningCombat`, `code:esv::combat::SurprisedSystem::SurpriseAlliesOfStealthAttackTargets`, `code:esv::combat::SurprisedSystem::IsAnyAlliedCombatGroupMemberSurprised` | confirmed for the update and the allies' gatherer, inferred for the joiners' |
| "Surprised" for these gatherers: the stealth request this tick, or an active status `SURPRISED` | `code:unplaced::IsSurprisedOrAskedToBe` | confirmed |
| Either request becomes a request to create `SURPRISED` on its holder for 6 seconds with no source; an immunity refuses it as it refuses any status | `code:unplaced::StatusRequestSystemGatherCombatEvents` | inferred |
| A character holding a status whose id is `SURPRISED` may not act on its turn | `code:esv::TurnBasedCanAct` | inferred |

The fixed string `SURPRISED` is named by these functions and no other of the executable's
pseudo-code (a whole-word search for its global): the engine has no second place that gives or
tests the status. Of the functions that name the two request components' type indices, the combat
system's update adds the stealth request and the surprise system's update the join request; the
others register or serialise them and were not each read in full.

The tick-by-tick shape, the same in every surprising scene (a hidden archer, two hostile humans):
the hit; two ticks later the three join, and the target has the stealth request; a tick later the
ally has the join request; four ticks after its request each holds the status, `turns` 1, no giver.

## 5. The scenes

Staged in `GustavX:Basic_Level_A`: two hostile humans 4 m apart, turned away from the host and
unkillable, 8 m from it unless said; the host a level-1 rogue with a shortbow and daggers. Read
from the game's rows (`event:game_hit`, `event:fight_joined`, `event:game_status`) and from the
creation of the two request components and of `eoc::hit::TargetComponent` with its attack flags.
Rows 1, 2, 4, 5, 10, 12 and 17 are tests; the others were staged once each.

| # | Attacker | Opener | Hit flag `FromSneak` | Hit before the joining | Target | Ally |
|---|---|---|---|---|---|---|
| 1 | hidden | arrow, hits | yes | yes, 2 ticks | surprised | surprised |
| 2 | hidden | arrow, misses (die 1) | yes | yes | surprised | surprised |
| 3 | hidden | dagger, beside the target | yes | yes | surprised | surprised |
| 4 | invisible | dagger, beside the target | yes | yes, 4 ticks | surprised | surprised |
| 5 | invisible | arrow | yes | no: the fight 6 ticks before the hit | not | not |
| 6 | hidden | Fire Bolt (attack roll) | yes | yes | surprised | surprised |
| 7 | hidden | Magic Missile (no roll) | yes | yes | surprised | surprised |
| 8 | hidden | Sacred Flame (saving throw) | yes | yes | surprised | surprised |
| 9 | hidden | Fireball on both, from 8 m and from 1.6 m | yes | yes | surprised, by its own request | surprised, by its own request |
| 10 | hidden | arrow at a target with Alert | yes | yes | not: the request, no status | surprised |
| 11 | hidden | a thrown acid vial | on the vial alone | the creature's hits 17 ticks after the joining | not | not |
| 12 | neither, the two see it | none: the fight begins by sight | - | - | not | not |
| 13 | a hidden hostile human | sword on the host | yes | yes | the host surprised | - |
| 14 | a hidden goblin, 25 m from the host | blade on a lone human of another faction | yes | yes | surprised | - |
| 15 | hidden | arrow; a third hostile human 37 m off shares the target's combat group | yes | yes | surprised | the third joins at once and is surprised |
| 16 | hidden | arrow; a third hostile human with no shared group is brought in during round 1 | yes | yes | surprised | the late joiner is not |
| 17 | hidden | arrow at a target already fighting a third party | yes | the target joins nothing | not | not |
| 18 | neither; the two blinded | arrow | no | no fight began at all | not | not |

Row 18 shows the flag following the attacker's state and not the target's sight; its outcome says
nothing more, because the blinded, unkillable target joined no fight.

### The arrow's flight and the game's speed

Row 5 turns into row 4 when the game runs faster. The server ticks about 30 times a second at any
speed, and an arrow's flight is game time: at speed 1 the invisible archer's arrow covers 8 m in
six ticks and the fight begins at the first of them; at speed 8 it covers them inside one tick, the
hit is on the target one tick before `INVISIBLE` leaves and the fight begins, and both humans are
surprised. `test:surprise/hit_before_the_fight` therefore runs at speed 1 and declares the ceiling.
The same race is why claims 8 to 11 read as they do: what they measure is whether the hit outruns
the start of the fight.

## 6. What Surprised does

| Effect | Measured |
|---|---|
| The surprised creature's turn begins and ends by itself with no choice and no action | a human's and the player's own character's first turn each end 30 ticks after they begin, with no input (`test:fight/npc/surprised`; section 5 rows 1, 13) |
| It leaves as the next round begins | `event:game_unstatus` 3 ticks after `event:round_started` of round 2 |
| The resources are blocked, not emptied | the surprised creature's action, bonus action, reaction and movement read full while it holds the status |
| The turn is ended by the status's name | read only: `code:esv::TurnBasedCanAct` |

Not measured: whether a surprised creature makes an opportunity attack or another reaction, keeps
concentration, and how its saving throws and the attacks against it roll.

## 7. The wiki's claims, marked

| # | Mark | Why |
|---|---|---|
| 1 | confirmed for actions; reactions not measured | section 6 |
| 2 | confirmed | section 6 |
| 3 | imprecise | the target's awareness is not tested; what is tested is a hit from a hiding or invisible attacker that exists when the target joins the fight (section 1, A) |
| 4 | imprecise | no round is declared in the code read: the status is held creature by creature, and a creature of the attacked side can be without it (section 5 rows 10, 16); its turn was not watched |
| 5 | confirmed for Alert (`test:surprise/alert`); the others by their data alone (section 3) |
| 6 | confirmed, from the data | section 3 lists them |
| 7 | wrong as stated | the targets were turned away and did not see the attacker in every surprising scene; a target that sees a hidden attacker starts the fight before the hit |
| 8 | confirmed | section 5 rows 3, 4 |
| 9 | imprecise | a hidden archer surprises from 8 m as well (row 1); the range matters for an invisible one only, through the arrow's flight (row 5) |
| 10 | imprecise | from hiding every spell tried surprised at 8 m (rows 6-9); the pattern fits an attacker revealed before the hit lands |
| 11 | wrong for a hidden caster | Magic Missile and Fireball surprised at 8 m (rows 7, 9); Eldritch Blast, Witch Bolt and Fear not staged; not staged from invisibility |
| 12 | confirmed | `test:surprise/from_hiding`, case "the arrow misses" |
| 13 | confirmed, and explained | the hit that carries the flag is on the thrown object (row 11) |
| 14 | wrong as stated | invisibility surprises with a blade and fails with an arrow from 8 m (`test:surprise/hit_before_the_fight`); hiding surprised with both |
| 15 | not decidable here | nothing in the code read or the data gives a warning; an ally in the fight is surprised unless it is immune, one outside it is surprised only through a party or a combat group |
| 16 | not measured | the data states it (`Assassinate_Ambush`) |
| 17 | not measured | |

## 8. Not varied, not known

- A party of more than one: a member joining while another is surprised (B.2, the party half).
- An attacker allied to its target (A.4) and a hit with no spell (A.3): read only.
- The difficulty: the code read takes the game's global switches only into the relation of two
  creatures; no scene was run on another difficulty.
- A target that sees a hidden attacker and yet is hit before the fight begins: not staged - seen,
  the hidden host is in a fight at once.
- Spells from invisibility; Eldritch Blast, Witch Bolt and Fear from hiding.
- What sets `FromSneak` in the code: observed from the attacker's status alone, the function not
  read.
- The story's ambushes and dialogues of section 3 are read from their texts; none was played.
- Whether a status of another name with the same four boosts ends the turn.

## 9. Script Extender

`eoc::hit::TargetComponent` is declared with size 0x78; the code read steps through its storage by
0xB8. The fields read through it - the spell at +0x10, the attack flags at +0x58 - gave the values
the scenes predict (flags 22 for a first and last attack from hiding, 18 without hiding). The two
request components are mapped as tags (`CombatSurprisedStealthRequest`,
`CombatSurprisedJoinRequest`) and their creation is seen through `Ext.Entity.OnCreate`. None found
wrong among what was touched; the size is a question for a comparison of builds, not held to the
bar of a bug here.

## 10. The API used

- `GK.EventLog.On("component", { components = ... })` refuses a one-frame component (it wants its
  destruction too), so the two requests were watched by a raw subscription. A one-frame component
  as a row of the family would let a test name the engine's request itself.
- `GK.Cheat.Attack` ordered while a fight of others is still opening is not carried out at speed 1
  and says nothing; `test:surprise/already_fighting` waits for the fight's first turn.
- `GK.Stage` has no verb for a passive; the tests add Alert by a raw `Osi.AddPassive` on a staged
  creature.
