# Demonstrations: what the roll machinery lets a mod do

Four scenes to run in a copy of the game on the test level (`up --level GustavX:Basic_Level_A`)
and watch. Each opens a stage scope (`GK.Stage.Begin()`), runs at the game's own speed, keeps its
state in a global `GKDemoN` (its `report` says what happened) and has an `_end.lua` chunk that
removes every listener it set and calls `GK.Stage.Clean()`. Needs the Script Extender build with
the roll events (`GK.Rolls.CanRig()` true). Run one at a time; end one before starting the next.

```
./gatekit --instance <copy> exec client "return GK.Camera.Look(0, { now = true })"   # 1, 3, 4: look north
./gatekit --instance <copy> exec server "$(cat research/demos/<name>.lua)"
./gatekit --instance <copy> exec server "return GKDemoN.report"                      # what happened
./gatekit --instance <copy> exec server "$(cat research/demos/<name>_end.lua)"
```

## 1. Five goblins and Magic Missile - `missile_redirect.lua`

**Seen:** five goblins in a ring 7 m north of Astarion; the fight starts, Astarion casts Magic
Missile, three darts fly at the far ("top") goblin. At the first dart the four others drop dead in
their blood; the top goblin stands at full health, and the combat log says it takes no damage.

**How:** `Ext.Events.DealDamage` (raised as a damage functor applies, with `Caster` and `Target`)
remembers the target; `Ext.Events.BeforeDealDamage` (raised inside it as the game throws the damage
event, with the computed `Hit` and `Attack`) zeroes `Hit.TotalDamageDone`, `Attack.TotalDamageDone`
and every `DamageList` amount when the target is the top goblin. The first dart also deals 1000
force to each of the four others with `GK.Stage.Damage(1000, goblin, "Force", true)`, a tick later.

**What the damage events allow** (read in `BG3Extender/Lua/Server/FunctorEvents.inl` and tried):
- the **amount**: yes. In `BeforeDealDamage` the hit's amounts are the game's own structures;
  written 0 the target loses nothing, written 1000 a 9-hit-point goblin dies.
- the **target**: no. `DealDamage.Target` is a copy of the handle; the hook calls the game with its
  own. `BeforeDealDamage` carries no target at all (hence the pairing with `DealDamage`).
- **cancel**: no. Stopping the event stops other Lua handlers, never the game's call; a zeroed hit
  is the way (the projectile still flies and the hit still lands, for 0).
So the 1000 to the others is the stage's damage verb, not a redirected hit.

## 2. A companion rolls the dialogue check - `companion_roll.lua`

**Seen:** a fighter joins Astarion's party; the counting-house guard speaks. Answer 1 (Persuasion,
DC 15) - by hand, or `exec ui "return GK.Answer(1)"`. The roll window opens over Astarion, but the
bonuses it lists are the companion's: Charisma +2 and +5, total +7 (Astarion's own: Persuasion +2).
Throw (click, or `exec ui "return GK.Gui.Resolve{ roll = 'throw' }"` until it continues): the
companion's total decides the guard's answer.

**How - the game's own way (b):** `Ext.Entity.OnCreateDeferred("RequestedRoll")` swaps the request's
`Roller` and `EntityUuid` for the companion before the window opens. The game then builds the whole
roll as hers: her modifiers in the window, the extender's `BeforeRoll`/`Roll` name her, and the
Osiris `RollResult` (the `game_roll` row) says "<companion>'s active Persuasion succeeds
(13 + 7 = 20 vs 15)". The `BeforeRoll` handler that writes her skill total (her `Stats.Skills`) as
the bonus - way (a) - is kept as a guard; it found the game's bonus already hers every run.
The companion is `Osi.AddPartyFollower` of the host (taken back by `Osi.RemovePartyFollower` at
Clean) with a boost `Skill(Persuasion,5)` through the ledger, so that her bonus differs.

**A trap the scene handles:** the game marks the speaker "rolling"
(`active_roll::InProgressComponent`, server and client) when the request is made and clears the
mark of the roller when the roll ends - after the swap, the companion's. Left on the host, the
stale mark (its `Entity` a dead roll) keeps the host's next dialogue check from opening its window,
and `GK.Stage.Verify` names it. The scene removes it (`RemoveComponent`) half a second after the
request is destroyed, and again at its end.

## 3. The foretold die - `foretold_die.lua`

**Seen:** two goblins charge Astarion (kept alive, healed when low; he only ends his turns). Before
each goblin's attack a notification says "Foretold: <goblin> - Main-hand attack at Astarion - 50% to
hit"; when the die lands another says "The die: 11 (total 14 vs 14) - HIT. Hits foretold 4.0, dealt
5 of 8": the chances, summed, set beside what the dice gave.

**How:** the family `fight` of the event log writes `fight_action` from the game's `SpellCastState`
about twenty ticks before the first die; `GK.Rolls.Chance{ who, spell, target }`
(`Ext.Stats.GetSpellRollChance`) gives the game's own chance; `Ext.Events.Roll` gives the die, the
total and the AC (`DC`). `Osi.ShowNotification` shows the text.

## 4. The cursed d20 - `cursed_d20.lua`

**Seen:** Astarion shoots on each of his turns, two goblins hack back. Every third attack roll of
Astarion lands a natural 1 - "Critical miss!" over the goblin even when the die thrown would have
hit - and every third goblin attack a natural 20 - "Critical hit!", doubled dice. A notification
says each time what the die showed and what the curse made of it.

**How:** `Ext.Events.Roll` alone: the handler writes `e.Natural`; the game moves the total and the
critical by its own rule after the handlers, so the verdict, the damage and the combat log follow.
(Inside the handler `e.Total` and `e.Critical` still read the thrown values.)

## What is not done

- A scene is not one camera shot: the fight's camera follows the active creature, a dialogue's the
  speaker, so the companion in 2 is in the window's numbers, not in the picture.
- Notifications compete with the game's own banners ("Your turn") and may be hidden by them.

## Not steady yet

The scenes were built and seen working in one copy of the game (numbers and screenshots in the
night's notes). In another copy, freshly started, the first scene asked its Magic Missile cast and
the game did not carry it out, twice of two: the fight was on and it was the host's turn; why is
not found. Treat the scenes as demonstrations to be run by a person who watches, not as tests.
