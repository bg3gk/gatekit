# The first fundamental tests: a proposal

A direction to aim the coverage at, not a fixed plan. It names the tests that everything else
stands on, as their addresses will read, with the question each answers about the game. The
present tests under `tests/` are a first draft; they are written anew from this tree, keeping what
they learned. Rules of a test: [docs/writing-tests.md](../../docs/writing-tests.md); of the API:
[docs/api-rules.md](../../docs/api-rules.md); the address form:
[for-the-documents.md](for-the-documents.md), "One address for each kind of thing". Below, a row
of the event log is written `event:<kind>`; "exists" means it is in
`mods/GateKit/.../Lua/Shared/EventKinds.json` today, "gap" that it is to be built.

Sources are named by kind: **wiki** (a bg3.wiki page), **study** (`research/studies/<name>.md`),
**owner** (a statement of the owner's, a hypothesis until a test holds it), **press** (an article
or a forum thread: a claim to test), **ours** (a measurement of the proof of concept).

## The tree

Folder / test - its cases. `_selftest/` (the toolkit's own checks in a game) stands apart and is
not counted.

```
rolls/
  verdict/
    natural_extremes        check, story_save, spell_save, attack, concentration_save, dialogue_check, death_save
    total_against_dc        check_meets, check_one_short, save_meets, attack_meets_ac
  advantage/
    kept_die                neither, advantage, disadvantage, both, two_sources, window_advantage, window_disadvantage, attack
  critical/
    threshold               twenty, nineteen, nineteen_lowered, paralysed_melee, crit_immune
  damage/
    dice                    pinned_every_die, critical_doubles_dice, flat_never_below_one
  pin/
    what_it_fixes           passive_check, story_save, window_check, attack, target_save, both_dice, bonus_die_free, rerolled_die
    binding                 once_check, once_save, asked_before_boost_left
  karmic/
    forced_success          dialogue_check, passive_check, attack, target_save
    debt                    grows_on_failure, success_pays, load_forgives
  window/
    blocking                dialogue_check, lock, trap
    add_bonus               own_guidance, member_guidance, story_check_allows_none
  streams/
    seed_and_count          generic, dialog, fight
    which_stream            passive_check, dialogue_check, attack, damage, enemy_attack
    load_reseeds            after_save, two_loads, cold_start
  inspiration/
    pool                    cap, one_for_the_party, earned_above_cap
    reroll                  dialogue, lock, trap, member_spends, at_zero
input/
  lock                      keyboard, mouse, gamepad, fair_press_passes, undone_by_clean
  gamepad/
    state                   keyboard_ui, controller_ui
    switch_gameplay         to_controller, to_keyboard, mouse_throws_back
    switch_menu             to_controller, to_keyboard, mouse_throws_back
    hold                    input_mode_option, our_verb, mouse_while_held
saveload/
  save                      quick_key, quick_command, menu, story_autosave
  second_save               (one run, no cases)
  load                      quick_load_newest, menu_load
time/
  clock                     speed_1, working, paused
  speed_limits              downed_raised, turn_before_hit
level/
  survivors_of_a_load       engine_switches, game_speed, frame_cap, party_state
  kept_components           story_boost_requests, unsheath_default, killer
  relations_after_attack    neutral_turns_hostile, faction_staged_later
combat/
  turn/
    order                   initiative_formula, tie, pinned_bonus, joined_late
    resources_spent         action, bonus, movement, reaction
    resources_recharge      turn_start, fight_end, out_of_combat, first_strike, rest_resource
  weapon/
    toggle                  set_in_hand, other_set
    interrupted             second_toggle_close, second_toggle_apart, walk, jump_aim
    put_away                fight_ended, killing_blow, aim_open, weapon_button
    in_fight                key, script
sight/
  party_perceives           in_view, far, invisible, staged_still
aiming/
  targeting                 target_spell, projectile, ground_point, two_clicks, in_fight
  cursor/
    hit_chance              plain, advantage, ground
    refusal                 target_conditions, out_of_range, no_movement
```

21 folders, 40 tests, 146 cases.

Added beyond the brief, and why:
- `rolls/verdict/total_against_dc` - "meets or beats" decides every roll that is not a natural 1
  or 20; no present test states it, and every combat and dialogue test reads a verdict.
- `rolls/damage/dice` - the damage roll is a roll of its own, pinned differently (every die), and
  the critical doubles it; the combat tests stand on it.
- `rolls/streams/which_stream` - which roll draws from which stream decides whether a rewind or a
  save can repeat a fight at all.
- `time/clock` - every limit of a wait is counted in game ticks; that the server ticks at a fixed
  rate whatever the speed is the ground of "events, not waits".
- `level/*` - what Clean and the ladder rely on that is the game's own behaviour (what a load keeps,
  what the engine never removes, what an attack leaves in the relations), held as game rules and
  not as our selftest.
- `combat/turn/order` - every fight test pins initiative; that the roll is a d4 and the pin a bonus
  (wiki against the tabletop) is to be shown once.
- `sight/party_perceives` - what a fair client may know, the preview of an area and a fair
  `GK.Gui.AimBest` all stand on whom the party sees.

## The tests

### `rolls/verdict/natural_extremes`
- **Rule.** A natural 20 succeeds and a natural 1 fails on an attack roll and an ability check
  whatever the bonus and the difficulty; a saving throw is decided by its total, except a
  concentration save and a save in a dialogue. Source: wiki "Dice rolls" (natural 1s and 20s),
  "Saving throws". Death saves: the wiki says nothing of a natural 20 or 1; the tabletop rule (a 20
  stands the creature up, a 1 is two failures) is the hypothesis.
- **Cases.** One per kind of roll; each pins the die at 20 against a difficulty no 20 reaches, and
  at 1 against a difficulty of 0 (or the reverse, by the kind's rule).
- **Hard.** The pin; Karmic Dice off (the pin does it); the difficulty out of the bonus's reach,
  read from the roll and stated as `need`. **Flexible.** Which creature rolls; how the difficulty
  is shifted (a boost of the game's data or a story DC).
- **Steps.** cheat: scene, pin, the shifted DC, the roll asked by the story (`Osi.RequestPassiveRoll`)
  or the fight; fair: the attack, the cast, the dialogue answer and the throw; observation: the row.
- **Rows.** `event:game_roll` (exists) for checks and story saves; `event:game_hit` with `attack`
  and `saves` (exists) for attacks and spell saves; `event:roll_ended` (exists) for the dialogue
  check. Gap: which row carries a concentration save and a death save - not read; to be found with
  the tracer over a damaged concentrating caster and a downed member.
- **API missing.** A self-tracking boost (`GK.Stage.Boost`, to be built) for the DC shift; a downed
  party member exists as a fixture.
- **Today.** check, story_save, spell_save, attack, dialogue_check: yes (the old tests did each).
  concentration_save, death_save: no, the row is not known.

### `rolls/verdict/total_against_dc`
- **Rule.** Between the extremes the total (natural die plus the listed modifiers) passes when it
  meets the difficulty; one short fails. An attack equal to the armour class hits. Source: wiki
  "Saving throws" (meet or beat), "Dice rolls"; ours (the roll system's verdict, study rolls §6).
- **Cases.** A check that meets, one short by 1, a save that meets, an attack equal to the AC.
- **Hard.** The pin and a DC set exactly at the total, read back from the roll before the verdict
  is judged (`need`). **Flexible.** The modifier used.
- **Steps.** cheat: pin, DC shift; fair: the attack; observation: `total`, `dc`/`ac`, `success`.
- **Rows.** `event:game_roll`, `event:game_hit.attack` (exist).
- **Today.** Yes.

### `rolls/advantage/kept_die`
- **Rule.** The game draws two d20 for every roll; advantage keeps the higher, disadvantage the
  lower, both together cancel and one counts as if neither; two sources of advantage still give two
  dice. Source: wiki "Advantage", "Dice rolls"; ours (study rolls §3: a second die on a plain roll
  too). The wiki gives passive scores a flat +-5, while the game draws two dice for its passive
  rolls: a disagreement, a finding worth this test.
- **Cases.** neither, advantage, disadvantage, both, two_sources (passive checks, the dice free);
  window_advantage, window_disadvantage (the roll window shows two dice); attack (a paralysed
  target).
- **Hard.** Karmic Dice off; the dice free (the rule is per roll: kept >= discarded), so one roll
  per case with advantage or disadvantage; neither and both need several rolls to show that no side
  is preferred - the repetition is the thing tested and stays small (the old 16 is the bound).
- **Steps.** cheat: the boosts (`Advantage(Skill,...)`, the game's data), Karmic off, the rolls asked;
  fair: dialogue answer and throw; observation: rows.
- **Rows.** `event:game_roll` with `natural`, `discarded`, `advantage` (exists); `event:roll` /
  `event:roll_ended` with `dice` (exists). Gap: an attack's discarded die is not in `game_hit`.
- **Today.** All but attack.

### `rolls/critical/threshold`
- **Rule.** A natural 20 on an attack is a critical hit; a 19 is not, unless an effect lowers the
  threshold (they stack); a melee attack on a paralysed target is always critical; armour that
  blocks criticals turns a 20 into a plain roll that can miss. Source: wiki "Critical hit".
- **Cases.** twenty, nineteen, nineteen_lowered (a threshold boost of the game's data, its name
  found with `gatekit data grep`), paralysed_melee (the die pinned at 2), crit_immune.
- **Hard.** Pin; a hostile target with no dialogue; the target's AC above 19 plus the bonus for
  crit_immune (`need`). **Flexible.** The item that blocks criticals, found in the data.
- **Steps.** cheat: scene, pin, boost, status; fair: `GK.Act.Attack`; observation: `game_hit`.
- **Rows.** `event:game_hit.attack.critical`, `outcome` (exist).
- **Today.** Yes once the boost and the item are found in the data.

### `rolls/damage/dice`
- **Rule.** A damage roll draws each die; a damage pin holds every die; a critical rolls the dice
  twice and adds the flat modifiers once; a strike of flat damage deals at least 1. Source: wiki
  "Critical hit", "Unarmed Strike"; ours (old `combat/melee/attack_by_die`, `unarmed/strike`). The
  wiki says Karmic Dice draws damage near the average: a measurement, kept apart (`--repeat`).
- **Cases.** pinned_every_die, critical_doubles_dice, flat_never_below_one.
- **Hard.** Attack and damage pins. **Rows.** `event:game_hit` `damage` (exists), `event:game_damage`.
- **Today.** Yes.

### `rolls/pin/what_it_fixes`
- **Rule.** The pin - the game's own pair of boosts that bound a roll of one kind - fixes the
  natural die of that creature's rolls of that kind and nothing else: the modifiers, the
  difficulty and the verdict stay the game's, both dice of an advantaged roll are held, a bonus
  die added in the window is not, a die thrown again for inspiration is. Source: the game's data
  (Reliable Talent is such a boost); ours (study rolls §1-§3). This is the honesty of a pin: an
  unpinned roll and a pinned one at the same die read alike.
- **Cases.** passive_check, story_save, window_check, attack, target_save (the pin on the target),
  both_dice, bonus_die_free, rerolled_die.
- **Hard.** Clean scene; the pin's `roll_pin` row before the roll; Karmic Dice off.
  **Flexible.** The difficulty.
- **Steps.** cheat: pin, inspiration, Guidance taught; fair: answer, throw, re-roll, add bonus;
  observation: rows; the same roll's modifiers read unpinned in the same case.
- **Rows.** `event:roll_pin`, `event:game_roll.pinned`, `event:roll_ended.added`, `rerolls` (exist).
- **API missing.** none. bonus_die_free has a declared ceiling of speed 1 (Guidance lands after the
  roll above it: ours); it is to be looked at as a suspicion against our timing first.
- **Today.** Yes.

### `rolls/pin/binding`
- **Rule.** A boost takes effect about 3 server ticks after it is added and leaves 3-4 ticks after
  it is removed; a roll's die is decided when it is asked for (passive) or thrown (window); so a
  `once` pin also decides a roll asked before its boost has left. Source: ours (lua-api §17).
- **Cases.** once_check, once_save, asked_before_boost_left.
- **Rows.** `event:roll_unpin` with `ticks` (exists). **Today.** Yes.

### `rolls/karmic/forced_success`
- **Rule.** With Karmic Dice on, failed rolls of the kinds it weights build a debt that forces a
  success above a pinned die; it weights attack rolls, targets' saves (making targets fail) and
  dialogue checks, not other checks. Source: wiki "Dice rolls" (Karmic Dice); ours (a dialogue
  check pinned at 1 came 17 after two to four failures).
- **Cases.** dialogue_check (forced), passive_check (never forced), attack (by the wiki),
  target_save (by the wiki: a forced failure of the target).
- **Hard.** `Pin{ karmic = true }`; the repetition is the thing tested, bounded (a forced success
  within N tries, N stated with its reason). **Flexible.** The check.
- **Rows.** `event:game_roll`, `event:game_hit` (exist). **Today.** dialogue and passive yes;
  attack and target_save: yes in principle, never tried.

### `rolls/karmic/debt`
- **Rule (hypothesis).** The debt is kept per stream (`FailDebt`, `SuccessDebt` of a stream: ours);
  the wiki keeps it separately for each player, allied creatures, enemies and dialogue; a success
  pays it; a load makes the streams anew and so forgives it.
- **Rows.** none needed beyond reads of `GK.Rolls.Streams()` after each `game_roll`.
- **Waits.** A study: whether `FailDebt` is the debt (a watch on it over a failure), which stream a
  fight's rolls weight - and the fork's search by value and changed fields in a trace if the watch
  does not find it.

### `rolls/window/blocking`
- **Rule.** An active roll is a blocking state of its own: the window opens when the game waits
  for the throw, nothing of what asked for the roll goes on while it is open (the dialogue's next
  node, the lock), it closes with its verdict, and what follows follows that verdict. Source: wiki
  "Dice rolls" (the roll menu in dialogue, locks and traps); ours (study rolls §2). Skipping the
  dice's animation, as a click does, changes neither die nor verdict - a step, not a test.
- **Cases.** dialogue_check (`GK.Kit.Talker{ dialog = "roll" }`), lock (`GK.Fixture` lock,
  `GK.Act.Lockpick`), trap (a trap fixture, its disarm).
- **Hard.** Pin; the window's opening row before any throw. **Flexible.** The DC.
- **Steps.** cheat: scene, pin; fair: start (answer, lockpick, disarm), throw; observation: rows.
- **Rows.** `event:roll`, `event:roll_ended`, `event:dialogue_choice` (exist). Gap: "nothing goes on
  while it is open" is an absence - it is shown by the order of rows (no `dialogue_line` and no
  lock change between `roll` and `roll_ended`), which holds only if the lock's opening has a row:
  `event:game_item`? not known; to be found.
- **API missing.** Whether `GK.Act.Lockpick` throws through the window (not proven live: open items).
- **Today.** dialogue_check yes; lock and trap in part.

### `rolls/window/add_bonus`
- **Rule.** Before the throw the window lets the roller and other members add bonuses of spells and
  items (Guidance: its own d4); some story checks allow none. Source: wiki "Dice rolls", "Guidance".
- **Cases.** own_guidance, member_guidance (a party of two), story_check_allows_none (a dialogue to
  be found: the wiki names the Dark Urge's checks).
- **Rows.** `event:roll.boosts`, `event:roll_ended.added` (exist).
- **Today.** own_guidance at speed 1 (declared ceiling); the other two wait for a second member
  staged and a dialogue found.

### `rolls/streams/seed_and_count`
- **Rule.** A stream's dice are a function of its seed and its count; written back, the same dice
  come again. Source: ours (study rolls §5, §6).
- **Cases.** generic (passive checks), dialog, fight (a player's spell and damage streams).
- **Rows.** `event:game_roll` (exists); reads of `GK.Rolls.Streams()`.
- **Today.** generic yes; dialog and fight not tried.

### `rolls/streams/which_stream`
- **Rule (ours, partly).** Passive checks of every roller share Generic; a dialogue check draws from
  the player's Dialog stream and two dice from Generic; a fight's attacks and damage from the
  Spell, Damage streams of player or NPC (the stream types Script Extender declares).
- **Cases.** passive_check, dialogue_check, attack, damage, enemy_attack.
- **Rows.** none: counts read before and after each roll row. **Today.** first two; the rest wait
  for a study of the fight's streams (open in study rolls §5).

### `rolls/streams/load_reseeds`
- **Rule.** A load seeds the streams from the clock in seconds, so the rolls after a load differ
  from those after the save and from another load; a cold start of a level always draws the same.
  Source: ours (study rolls §4); press: players both reload to throw again and report repeats.
- **Cases.** after_save, two_loads, cold_start (`reset = "game"`).
- **Stands on.** `saveload/save`, `saveload/load`.
- **Rows.** `event:loading`, `event:loading_ended`, `event:game_roll` (exist). **Today.** first two yes.

### `rolls/inspiration/pool`
- **Rule.** Inspiration is one pool for the whole party, at most 4; a point earned above 4 is lost
  (the experience still given). Source: wiki "Inspiration"; owner (one pool, four, hypothesis);
  ours (the resource is held by the party's entity, six given held four).
- **Cases.** cap, one_for_the_party (a party of two reads the same pool), earned_above_cap.
- **Rows.** none of its own: `GK.Rolls.Inspiration()` read after the give. Gap: the give has no
  row; `Osi.GiveInspirationPoints` is a cheat and undone only by writing back.
- **Today.** cap yes; the rest need a second member and the way a point is earned (the wiki: a deed
  of the background) - not part of covering rolls, so earned_above_cap uses the give.

### `rolls/inspiration/reroll`
- **Rule.** After a failed roll of the roll window, "Roll Again" is offered while a point is held,
  draws a new die, keeps the first throw's modifiers (advantage and Guidance among them), spends
  exactly one point, and is not offered at zero; normal combat rolls are not eligible. Source: wiki
  "Inspiration"; open items ("Inspiration in a roll").
- **Cases.** dialogue, lock, trap, member_spends (who spends in a party of two: open), at_zero.
- **Rows.** `event:roll_ended.rerolls`, `inspiration`, `event:game_roll.inspired` (exist). Gap:
  "offered" - the window's Roll Again availability is not in `event:roll`; to be added to its
  payload from the window's own state.
- **Today.** dialogue yes; lock and trap with `rolls/window/blocking`; member_spends waits.

### `input/lock`
- **Rule (to be established).** A lock keeps every outside signal - keyboard, mouse, gamepad - from
  the game while our own fair actions still pass; its state can be read and the ledger undoes it.
  Source: owner (open items, "Input"). It is a verb of ours, but what it proves is where the game
  takes its input in.
- **Cases.** keyboard (a posted key message, `game.key`), mouse, gamepad, fair_press_passes
  (`GK.Act.Press`, the engine's own press), undone_by_clean.
- **Steps.** cheat: the lock (it bends the world); fair: our presses; outside: the host's key
  message, a real or virtual device; observation: what the game did (a weapon toggle as the sign:
  `event:weapon_state`, exists).
- **Rows.** Gap: `input_lock` begin and end rows; a row for an input the game received.
- **Waits.** A study of where input enters (the instruments over a key press); a session with the
  owner for a real device; posted mouse messages are ignored by the game anyway (docs/facts.md), so
  mouse needs real input. Started on the owner's word only.

### `input/gamepad/state`
- **Rule.** The game is in one of two interfaces, keyboard and controller, and says which.
  Source: docs/facts.md (the engine's switch functions); `GlobalSwitches.ControllerMode`, read in
  the bridge's status today.
- **Rows.** Gap: `input_mode` row on a change (its event to be found; a read on the tick last).
- **Today.** The read exists; the controller case needs the switch.

### `input/gamepad/switch_gameplay`, `input/gamepad/switch_menu`
- **Rule (press, hypothesis).** With the game's input mode on Automatic, the interface follows the
  last device used; any mouse input throws it back to the keyboard interface. Two tests because
  the venues differ (a level; the main menu).
- **Cases.** to_controller, to_keyboard, mouse_throws_back.
- **Waits.** A device (owner's session, or a virtual controller); the fork's
  `SetControllerMode` call only on the owner's yes; where in the code the switch lives is the
  study's last step.

### `input/gamepad/hold`
- **Rule (press, hypothesis).** The options hold an "Input Mode" of Automatic, Keyboard only and
  Controller only; Controller only keeps the controller interface through mouse input (one Steam
  thread says it also shuts the keyboard and mouse out). A verb that holds the interface wanted is
  to be built on what this test finds.
- **Cases.** input_mode_option (a setting: fair), our_verb (cheat), mouse_while_held.
- **Waits.** As above.

### `saveload/save`
- **Rule.** Saving is a state of the game's own with a sharp beginning and end; while it lasts a
  second save is refused (owner: the turning mark at the top left); when it ends the save's file
  is there. Source: owner (open items, "Saving"); ours (`GK.Gui.SaveAs` answers on the client's
  game state passing through Save). Press: quick save is F5, absent in Honour Mode.
- **Cases.** quick_key (`game.key("f5")`, fair), quick_command (the hotbar's `QuickSaveCommand`
  through `GK.Gui.QuickSave`, fair; the trigger deeper in the code is to be found), menu
  (`GK.Gui.SaveAs`: what counts is what follows the Save button), story_autosave (`Osi.AutoSave`,
  cheat - the checkpoint's own way).
- **Hard.** A level running, nothing blocking. **Flexible.** The save's name.
- **Steps.** fair: the save; observation: the rows, then the file once (a read after the end row,
  not a wait: the claim is that the file is there when the state ends).
- **Rows.** `event:saving`, `event:saving_ended` (exist: from the game state machine, both sides) -
  that quick save passes through the same state is not yet shown. Gap: a refused second save has
  no row (an absence: shown by one `saving_ended` and one new file after two requests); the turning
  mark's widget is not named (`event:window`?).
- **API missing.** A host read of a copy's new save folders by name (`gatekit save` lists them; a
  `game.*` form is to be checked). `GK.Gui.QuickSave` answers what it attempted, not what it saw.
- **Today.** In part: quick_command, menu and story_autosave yes; quick_key's key message in the
  background is untested; the refusal waits for the owner's answer below.

### `saveload/second_save`
- **Rule (owner).** A second save of the same world after a reset differs from the first only in
  what the game stamps (timers, fresh identifiers); the difference is studied, not fought.
- **Stands on.** `saveload/save`, the ladder's `venue` rung.
- **Steps.** fair: save; cheat: the reset; fair: save again; observation: the two files compared.
- **API missing.** A reader of the save package in C# (the format is described widely and read by
  LSLib); until then only sizes and the save window's listing compare.
- **Today.** Only the listing.

### `saveload/load`
- **Rule.** A load passes through a loading state with a sharp beginning and end and gives the
  saved world; quick load loads the newest save of any kind, not the newest quick save. Source:
  press (PC Gamer, Destructoid: F8, the newest save).
- **Cases.** quick_load_newest (a quick save, the host moved, a named save, F8: the host stands
  where the named save had it), menu_load (`GK.Gui.LoadLatest`).
- **Rows.** `event:loading`, `event:loading_ended`, `event:level_started` (exist).
- **Today.** Yes; the scope is lost at a load by design and the test opens a new baseline (as the
  old `rolls/save/load`).

### `time/clock`
- **Rule.** The server ticks about 30 times a second at any speed; the speed lengthens the game time
  of a tick; a paused game (hypothesis) ticks and adds no game time. Source: ours (lua-api §18).
- **Cases.** speed_1, working, paused.
- **Steps.** cheat: speed, pause; observation: `GK.Speed.Clock()` at two `event:game_state` rows.
- **Rows.** `event:game_state` (exists). **Today.** Yes; declares `fixed` (decided, not built).

### `time/speed_limits`
- **Rule.** What the game itself breaks above the working speed: from 16 the party-defeated check
  fires while a downed member is raised; from 24 a turn starts before the previous hit lands.
  Source: ours (lua-api §18).
- **Cases.** downed_raised (12 against 16), turn_before_hit (16 against 24).
- **Rows.** `event:gameover`, `event:game_turn`, `event:game_hit` (exist). **Today.** Yes, at the
  risk of a game-over box (a load is its way out).

### `level/survivors_of_a_load`
- **Rule.** A level load rebuilds the level from its save; the engine's global switches (Karmic
  Dice among them) and the game speed outlive it, the client's frame cap and the party's state do
  not. Source: ours (lua-api §14, "What survives a level load"; `scenarios/stage_level_survivors`).
- **Rows.** `event:loading`, `event:loading_ended` (exist). **Today.** Yes.

### `level/kept_components`
- **Rule.** The engine adds some components at a creature's first use of a system and never removes
  them, holding their default. Source: ours (`Server/GameKeeps.json`, lua-api §14).
- **Cases.** story_boost_requests (first `Osi.AddBoosts`), unsheath_default (first weapon request),
  killer (first kill).
- **Rows.** `event:component_created` (exists; the family on for the scope). **Today.** Yes.

### `level/relations_after_attack`
- **Rule (ours).** An attack on a neutral creature adds hostile entries to the server's relation
  tables, which the fight's end does not take away; a creature of that faction staged later is the
  party's enemy. **Waits.** The study "what a neutral creature does when attacked"; the tables'
  names; the fork's addresses of what is not a component, to watch who writes them.

### `combat/turn/order`
- **Rule.** Initiative is a d4 plus the Dexterity modifier and the initiative bonuses; a tie goes to
  the higher Dexterity; one player's characters with equal initiative act together; a creature that
  joins in the first round takes a full turn but only ahead of those yet to act, later it skips its
  first turn. Source: wiki "Initiative". The pin is a bonus (`Initiative(n)`), not the die (ours).
- **Cases.** initiative_formula, tie, pinned_bonus, joined_late.
- **Rows.** `event:game_turn` (exists). Gap: the initiative rolled is in no row.
- **Waits.** A study (initiative was dropped in the proof of concept): where the roll is kept.

### `combat/turn/resources_spent`
- **Rule.** An attack spends the action, an off-hand attack the bonus action, a walk the movement,
  an opportunity attack the reaction. Source: wiki "Resources".
- **Rows.** Gap: no row when a resource changes (`stage_refill` is ours); to be built from the
  replication of the action resources. **Today.** action and bonus yes; movement waits for fair
  movement, reaction for the opportunity attack (both open in "Acting as a player").

### `combat/turn/resources_recharge`
- **Rule.** Action, bonus action and reaction refill at the start of a turn and when a fight ends;
  out of combat a spent one comes back almost at once (ours: a round later, while the cooldown
  tracking component is held); a resource used to start a fight is not refilled on the first turn
  ("disable free first strikes"); rest resources do not refill with turns. Source: wiki
  "Resources"; ours (lua-api §14, the table "After an action").
- **Cases.** turn_start, fight_end, out_of_combat, first_strike, rest_resource.
- **Rows.** `event:game_turn`, `event:combat_ended`, `event:component_destroyed` (exist); the
  resource row of the test above (gap). **Today.** By reads at those rows.

### `combat/weapon/toggle`
- **Rule.** The player's toggle is a request the server carries out two frames later, whatever
  animation follows; it draws the weapon set in hand. Source: study weapon-in-hand §4, §7.
- **Cases.** set_in_hand, other_set (the set staged: the set's own key is now and then not taken).
- **Rows.** `event:weapon_state` (exists); the request as `event:component_created` of
  `ServerUnsheathUserRequestPending` (exists, the family on). **Today.** Yes.

### `combat/weapon/interrupted`
- **Rule.** Every toggle is carried out by the logic at once and in full; a second toggle within a
  server tick or two of the first is dropped (no request made); toggled back soon after, the model
  never shows the weapon drawn (owner, held: "on the back at once"); a walk does not interrupt; an
  aim begun in the same tick neither waits nor cuts the draw. Source: study weapon-in-hand §7; owner.
- **Cases.** second_toggle_close (1 server tick apart: dropped), second_toggle_apart (several ticks:
  both taken, the model stays sheathed), walk, jump_aim.
- **Change from the old test.** Time the two presses in server ticks, not in game seconds: then the
  case holds at every speed and the ceiling of 4 is not needed.
- **Rows.** `event:weapon_state` (exists). Gap: the client's visual state has no event (read each
  tick with `expect`: a recorded gap). Where the second press is dropped waits for changed fields in
  a trace. **Today.** Yes (walk is a cheat step: fair movement is not built).

### `combat/weapon/put_away`
- **Rule.** The game puts the weapon away by itself once nothing holds it out: in the tick a fight
  ends; after a killing blow, the tick the attack's animation component goes; with an aim open, the
  tick its animation component goes; after a hotbar weapon button, the same. Source: study
  weapon-in-hand §4, §7.
- **Rows.** `event:weapon_state`, `event:combat_ended`, `event:component_destroyed` (exist).
- **Today.** Yes.

### `combat/weapon/in_fight`
- **Rule (ours, seen twice).** On the actor's turn in a fight the key makes no request and a
  script's sheathe is not applied. **Rows.** Gap: an absence needs a row after it within the step
  (a following press of another key that writes one). **Today.** After that is built.

### `sight/party_perceives`
- **Rule.** The party perceives a creature a member sees, within sight range, not invisible; a
  creature created and paralysed in one chunk stays unseen until it moves. Source: wiki "Invisible",
  "Sight mechanics"; study aiming rung 5; the last a curiosity of ours.
- **Cases.** in_view, far, invisible, staged_still.
- **Rows.** Gap: no row when the sight set changes; `GK.See.PartyPerceives` reads it. **Today.** Yes.

### `aiming/targeting`
- **Rule.** Using a slot opens the targeting; leaving it (the cancel a right click makes) leaves no
  cast of the caster on the server or the client; a throw is aimed in two clicks. Source: ours
  (`_selftest/aim/not_taken`; the orphaned preview cast after a ground aim, seen and not reproduced).
- **Cases.** target_spell, projectile, ground_point, two_clicks, in_fight.
- **Rows.** Gap: no row when a targeting opens and closes; the server's preview cast is
  `GK.Stage.PreviewCasts()` read after the cancel's answer; its component's destruction is the
  event to use. **Today.** Yes with reads.

### `aiming/cursor/hit_chance`
- **Rule.** The chance the cursor shows over a creature is the attack roll's; with advantage it is
  1 - (1 - p)^2; over the ground a single-target spell shows none. Source: wiki "Advantage",
  "Paralysed". **Today.** Yes.

### `aiming/cursor/refusal`
- **Rule.** The cursor refuses an aim the action's target conditions exclude, one beyond range, and
  one that needs more movement than is left, each with its reason, and the click does nothing.
  Source: the game's stats (TargetConditions); study aiming rungs 1, 3. Which bit of the cursor's
  error mask is which waits for the fork's field sizes and paths. **Today.** Yes.

## The order

1. `time/clock` - every limit counts ticks; the working speed is set by every later test.
2. `rolls/pin/what_it_fixes` - every roll test and every combat test decides its die with it.
3. `rolls/verdict/natural_extremes` - the verdict every later reading judges.
4. `saveload/save` - the owner's first of the folder; `load`, `second_save` and
   `rolls/streams/load_reseeds` stand on it.
5. `rolls/window/blocking` - the blocking state that inspiration, bonuses and dialogue stand on.

Then: `rolls/verdict/total_against_dc`, `rolls/pin/binding`, `rolls/advantage/kept_die`,
`rolls/critical/threshold`, `rolls/damage/dice`, `saveload/load`, `rolls/streams/*`,
`rolls/karmic/forced_success`, `rolls/inspiration/*`, `rolls/window/add_bonus`, `level/*`,
`combat/turn/resources_recharge`, `combat/weapon/*`, `sight/party_perceives`, `aiming/*`,
`time/speed_limits`.

Waiting:

| Test or case | Waits for |
|---|---|
| `input/*` | a study of where input enters; a session with the owner and a real device; the fork's switch call on the owner's yes |
| `rolls/karmic/debt` | a study; the fork's search by value and changed fields in a trace |
| `level/relations_after_attack` | the neutral-creature study; the fork's addresses of non-components |
| `combat/turn/order` | a study of where the initiative roll is kept |
| `combat/weapon/interrupted` (where the press is dropped) | changed fields in a trace |
| `aiming/cursor/refusal` (the mask's bits) | field sizes and paths |
| `saveload/second_save` (contents) | a reader of the save package |
| member_spends, member_guidance, one_for_the_party | a second party member staged |
| concentration_save, death_save | the row that carries them |
| movement, reaction of `resources_spent` | fair movement; the opportunity attack |

## What the old tests proved that carries over

- The pin's pair of boosts fixes the natural die, passive or in the window; both dice of an
  advantaged roll; not a bonus die; a re-roll is pinned too; a `once` pin also catches a roll asked
  before the boost left.
- Karmic Dice overrides a pin on a dialogue check after two to four failures.
- The game draws two d20 for every roll; the kept and discarded dice are in the roll's request.
- A load seeds the streams from the clock in seconds; a cold start draws the same dice; a stream
  written back repeats its dice.
- One finished roll per window, the last throw, marked as inspired; inspiration is the party
  entity's resource, capped at 4.
- A spell's save is decided by the total; a critical doubles dice not modifiers; a flat strike
  deals at least 1; an off-hand attack keeps a negative modifier.
- A raw Osiris cast skips the roll and the cost; `GK.Cheat.Attack` rolls and pays.
- The weapon's logical state changes two frames after a request; a second toggle within a tick or
  two is dropped; the model stays sheathed on a quick toggle back; animation components hold it out.
- The cursor judges the walk; reach and range include both hit radii; the area's preview counts only
  the seen; an unseen creature on the line catches a projectile.

Dropped as tests for the sake of a test (each kept as a step, a case or a selftest):
- `_selftest/keys/toggle_taken` (twenty presses of our verb): one press is a step of `combat/weapon/toggle`.
- `_selftest/steps/fair_attack`: the same as `_selftest/clean/attack_in_fight`.
- `rolls/advantage/pin`, `rolls/dialogue/advantage`, `rolls/dialogue/reroll`: cases of
  `rolls/pin/what_it_fixes`, `rolls/advantage/kept_die`, `rolls/inspiration/reroll`.
- `rolls/pin/ledger`: our ledger, a selftest.
- `combat/melee/scripted_attack`: a check of our cheat, a selftest.
- The combat, area, reach and blocking tests are not dropped: they come after this tree, rewritten
  on it.

## Open questions for the owner

1. **Where the gamepad lives.** The open items name `tests/input/lock` and `tests/gamepad/state`,
   `switch-gameplay`, `switch-menu`. This proposal puts them under `input/gamepad/` with
   underscores. Which do you want? It fixes the addresses before any file exists.
2. **The game's Input Mode option.** A Steam thread names Automatic, Keyboard only and Controller
   only in the options, Controller only keeping the controller interface through mouse input. Is it
   there in the current build, and is "holding the interface" meant to be that setting (fair) or a
   verb of ours? If the setting holds, `input/gamepad/hold` needs no switch in the fork.
3. **A virtual controller.** May a test use a virtual gamepad driver (a download) for the outside
   signals, or is a real device in a session with you the only way? It decides whether
   `input/*` can run unattended.
4. **What counts as an outside signal.** The host's `game.key` posts key messages to the window;
   `GK.Act.Press` injects through the engine. Must the lock keep out `game.key` too? It decides
   whether the lock sits at the window's messages or at the engine's input.
5. **A second save while saving.** From your play: does a second F5 during the turning mark do
   nothing, wait and save again, or make two files? It is the expected outcome of the refusal step
   of `saveload/save`.
6. **Where saving is refused.** In a dialogue, on an enemy's turn, in a scene? Any you know from
   play would make a `saveload/refused` test with a clear rule.
7. **Quick load.** Press articles say F8 loads the newest save of any kind. Does that match your
   play? It is the rule of `saveload/load/quick_load_newest`.
8. **Who spends inspiration.** In a party of two, does the window let the roller only, or any
   member, spend the point? It is the expected outcome of `rolls/inspiration/reroll/member_spends`.
9. **Initiative.** The wiki says a d4 plus Dexterity. Is initiative among the first tests now
   (it was put off in the proof of concept)? It decides whether `combat/turn/order` waits.
10. **`level/` or `stage/`.** The game's behaviour that Clean relies on is put under `level/` as the
    game's notion; the stage is ours. Agreed? It names three tests.
11. **Speeds that break.** May `time/speed_limits` run the game at 16 and 24 on purpose, a game-over
    box and a load included? It decides whether that test exists or stays a measurement.
12. **The save package.** May the toolkit gain its own reader of the save file in C# (the format is
    documented by others)? Without it `saveload/second_save` compares only sizes and listings.

## The owner's answers so far

Statements from his play are hypotheses with their source, checked like any other.

- **A second save while saving (5).** The game does not save over a save in progress. In the menu
  the "Save" button goes inactive and, as he recalls, a hint says that saving is under way. So:
  the second request is ignored and the player is told. `saveload/save` gains the case
  `second_request_refused` (by the key and by the menu's button).
- **Where saving is refused (6).** As far as he has seen, only while a save is in progress: it
  works in dialogues, in fights, in scenes and, he believes, inside a roll window. The one other
  place he expects is the blocking state of the party's death. So `saveload/refused` is not a
  test of its own yet; `saveload/save` gains cases for the places where it is allowed (dialogue,
  fight, roll window) and one for the death state.
- **Holding the interface, and the lock (2, 4).** Holding is not a part of the game: it belongs
  to the testing of our API. The requirement is one, for the window in focus and out of it: the
  game takes no position of the real mouse (no cursor shown, no object highlighted) and ignores
  every command from outside - keyboard, gamepad, mouse - while our own fair actions pass. So
  `input/gamepad/hold` and `input/lock` are one subject; where the lock sits is for the study to
  find, with that requirement as its measure. He offers to act with a real keyboard and mouse at
  the moments a test asks for. A probe he proposes for the lock: a named action of the hotbar
  with a key of its own (the jump, Z) - sent by us it is taken, pressed by him on the real
  keyboard it is not. The game's own Input Mode option is at most a means, not the
  answer.
- **Inspiration (8).** At the basic level for now: the cap and the spending.
- **Initiative (9).** At the basic level, as a part of `rolls/` (the roll itself: its die, its
  bonus, the pin), not the whole order of a fight. The Alert feat adds to initiative (the wiki
  gives +5): it is one case here, as a source of a bonus, to show that a bonus is added. The
  rest about the feat belongs to a later test of feats.
- **The save package (12).** Not decided. The API may later keep a store of its own inside saves,
  and the subject will be deepened then. For now only the most basic: no reader of the package;
  `saveload/second_save` compares what the listing and the sizes give.
- **Speeds that break (11).** Yes: the game is to be tried at 16 and 24 on purpose, in a copy.

## Revision after the real coverage of rolls

A proposal, as the rest of this document is. The stage that read the roll machinery
(`research/studies/rolls.md`, sections 8-18) left instruments the tree above did not have, and
tests that stand. This section says what that changes.

### What stands (under `tests/`, each run at `--speed 1` and `--speed working`)

```
rolls/
  log/
    origins                 passive check, dialogue check, status applied, status on its turn, weapon attack,
                            spell attack and damage, target's save (the pair of a d20: the first kept,
                            the second discarded), three rays, creature's attack
    adjusted                (one run) an interrupt sets the roll after the throw: thrown 15, kept 1
  rig/
    one_of_several          nth of four (the third of four checks in one tick), by spell (the save
                            against one spell of two)
  streams/
    chosen_die              passive check, attack 20, attack 1
  chance/
    agrees_with_dice        melee, ranged: the game's own chance against the dice then thrown
  karmic/
    forced                  (one run) a roll forced by Karmic Dice has no dice of its own
fight/
  log/
    intent                  (one run) the cast is in the log before its die, its id joins the hit
    options                 (one run) what the AI weighed, its choice, the chance against the dice
    membership              (one run) joined at the start and under way, left killed (the death's
                            row names the killer too) and because the fight ended
    reaction                (one run) an attack of opportunity with what set it off
    resisted                die 7: what was rolled and what was dealt against a resistance
    unlisted                (one run) the dice of a stats expression are in the hit, the terms add up
  npc/
    without_the_party       (one run) two creatures fight to a death with no party member
    surprised               what Surprised does to the first turn
    asleep                  sleeping enemies start no fight; a fight keeps its members awake
surprise/
  from_hiding   hit_before_the_fight   already_fighting   alert
wake/
  two_distances   one   place
camera/focus/
  matrix                    point near, object middling, point far, object far, npc far; each
                            with the return to the host at the old distance
  two_targets   in_a_fight   far
gui/
  look/main_menu
  look/creation             (one run) lists, a slider, a swatch and its panel, the point buy, the way on
  tips/ability   tips/chain
figure/
  client_dummy   client_animation   client_effect
store/kept/
  round_trip                (one run) what a store holds at a save is what it holds after the load
show/
  picture   menu
quirks/
  attack/weapon_set_in_hand
  damage/critical_flag_of_a_sum
  damage/ground_row
  damage/resistance_floor   weapon_of_one, three_becomes_one, fire_bolt_of_one
```

Out of the tree, and where each is: the toolkit's own checks that need a game are self tests
(`tests/_selftest/rolls/log_unknown`, `fight/options_verb`, `say/overhead`, `store/refused`); the
three books placed again from a store alone are a demonstration (`research/demos/three_books.lua`,
its result in `research/studies/save-kept-data.md`); the wiki's list of bugged actions compared
with the game's data is a `gatekit data` query (`research/studies/rolls.md`, sections 23 and 27)
and no test; the experiment of one fight shared by several cases is recorded in
`research/rolls-todo.md` ("The experiment's result").

### What the new instruments change in the proposed tests

- **The roll log is the row to read.** Where a proposed test read `event:game_roll` or
  `event:game_hit` and could not see a die that was thrown and not kept, `dice_thrown` gives
  every die, the stream, who asked and what for. `rolls/advantage/kept_die` becomes one roll a
  case: both dice are in the row, kept and discarded, whatever the verdict. `rolls/damage/dice`
  reads the dice of a damage roll directly. `rolls/streams/which_stream` is the cases of
  `rolls/log/origins` and is not needed as a test of its own.
- **One throw is decided by the rig, not by a pin.** `rolls/verdict/natural_extremes`,
  `total_against_dc` and `critical/threshold` want "this roll is a 20": a rig names the roll by
  who, kind and stream and leaves every other roll alone, so a case no longer needs Karmic Dice
  off for the whole scope nor a pin taken off afterwards. The pin stays for a standing rule
  ("every check of this creature") and for stock Script Extender.
- **`rolls/pin/binding` loses its reason.** "Once" was the pin's way to one roll; with the rig
  the cases `once_check` and `once_save` are the rig's `times = 1`. What stays of the test is
  one case: a pin asked before the boost of an earlier pin has left.
- **`rolls/karmic/forced_success` can say "forced" from the row** (`forced = true`, no dice
  drawn), and `karmic/debt` can read the debts of a stream beside it.
- **The verdict has a second chapter.** An interrupt may change a roll after the throw; a test
  of a verdict on an attack or a save states which it reads - the throw (`dice_thrown`) or what
  the game goes on with (`dice_adjusted`, the hit).

### Proposed next, in the order of what they would catch

```
rolls/
  log/
    hidden                  failed_perception_shown, failed_perception_hidden, stealth_check, athletics_shown
    after_the_throw         set_roll, adjust_roll (Bardic), reroll (Lucky), boost_reroll (Halfling Luck: inside the throw)
    one_action_many_rolls   extra_attack, off_hand, scorching_ray, concentration_after_damage
  rig/
    one_of_several          + by status (a save of this status only: the rig told by `status`)
  generator/
    predicted               the next die of a stream said before it is thrown: check, attack, damage (Karmic off)
  verdict/
    natural_extremes        ... the proposed cases, each with a rig
```

- `rolls/log/hidden` - "The combat log leaves out what the ruleset says to hide, and the roll
  log does not." Source: the ruleset modifier `HIDE_FAILED_PERCEPTION_ROLLS` (study, section
  14); the combat log's entry counted through the game's own table of entry types. It would
  catch a roll that is thrown and shown nowhere.
- `rolls/log/after_the_throw` - "Each way an interrupt changes a roll is in the log as thrown
  and as kept." It would catch a kind of change that does not pass the one function hooked.
- A rig named by `spell` stands as the case `by spell` of `rolls/rig/one_of_several`; by `status`,
  `source` and `target` the rig matches too, and no case holds them.
- `rolls/generator/predicted` - "The die a stream gives next is the generator's" (study,
  section 8): `GK.Rolls.Next` against the row. It would catch a patch that changes the
  generator, which every rewind of a stream depends on.

Not proposed, and why: a death saving throw (not stageable in the test level: open items); the
order of initiative with equal rolls (belongs to `combat/turn/order`).

### Proposed: tests that hold a known fault of the game as it is

The sweeps of the night (`research/studies/rolls.md`, sections 20 and 23) found places where
the game does not do what its text or the wiki says, each reproduced with rigged dice. A test
that states the fault as found - its rule is "the game does this, and the wiki says that" -
turns a patch that mends it into a failing test, which is how a tool that leans on the fault
(a damage preview, a log reader) learns of the change. Proposed as a tree of its own, so that
its failures are read as news of the game and not as faults of the toolkit:

```
quirks/
  damage/
    resistance_floor        fire_bolt_of_one, weapon_of_one, three_becomes_one   (1 stays 1; the wiki rounds down to 0)   stands
    critical_flag_of_a_sum  (the flag is of the sum of the dice)                 stands
    ground_row              a_miss_still_throws_it   (thrown on every weapon attack, never takes hit points)   stands
  attack/
    weapon_set_in_hand      melee_asked_with_the_bow, sand_toss      (Strength used in place of the weapon's ability)   stands
    ai_weighs_the_set       goblin_in_its_ranged_set                 (the scorer's number against the dice)
```

Each case names its source (the wiki page and section, or the study) in its leading comment and
asserts the numbers found. What the sweeps proposed here and is not a fault of the game stands
elsewhere or nowhere: the dice of a stats expression are in the hit (`fight/log/unlisted`); a
damage bonus of a boost is doubled on a critical (the finding was taken back: the study, section
27); a forced roll's die is the wiki's own rule (`rolls/karmic/forced`); the second d20 of every
roll is the study's section 3 (`rolls/log/origins`); the wiki's list of bugged actions is read
from the game's data by a query, not by a test.
