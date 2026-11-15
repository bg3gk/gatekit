# Executor attacks: the chronicle

A work record, appended per rung. Copy `attacks`, branch `poc-attacks`.

## Rung 1: a fair melee attack inside a fight, and the verb

**Asked.** A fair melee attack in a fight: spends the action; pinned 1 deals nothing; pinned to
hit deals the weapon's dice and modifier; pinned 20 is a critical with critical damage. Beside it
the contrast: the scripted attack hits against a pinned 1 and spends nothing. Then one verb, fair
`GK.Act.Attack` (and `Cast`), the player's path, answering on the event log. And first: why
`tests/_selftest/clean/attack_in_fight.lua` once failed with "no character of this client is
controlled".

**Done, and how it is proven.**
- `tests/combat/melee/attack_by_die.lua`: die 1 critical miss, 0 damage, action spent; die 19 hit,
  damage pinned at 4 = 7 = the cursor's maximum; die 20 critical, 11 = 7 + 4 (dice twice, modifier
  once); the turn's start gives the action back. Live, one session, speed 1 then working speed
  (`--speed 1`, `--speed working`), passes, scene clean after (ladder's clean held).
- `tests/combat/melee/scripted_attack.lua`: a raw `Osi.UseSpell` attack hits at a pinned 1 and
  the game charges no action; `GK.Cheat.Attack` at a pinned 1 is a critical miss and the game spends
  the action. Live at both speeds. **The brief's premise holds for the raw Osiris cast only**:
  `GK.Cheat.Attack` strips the skipping options and therefore rolls and pays. The Act.lua comment
  that said otherwise is corrected.
- `GK.Act.Attack(target, options)` / `GK.Act.Cast(spell, target, options)` on the UI side
  (new file `Client/Act.lua`): UseSlot, Aim, Confirm, then the game's finished-cast event of the
  caster's cast entity on the client; the answer carries the outcome per target and what the cursor
  showed. Tried on: miss, hit, critical; refusals answered by the game's words. Not yet tried on a
  second unlike case (a ranged attack, a spell) - that is rung 3's first use.
- `game_hit` rows (Server/Act.lua, kind added to Shared/EventKinds.json): every hit the game
  resolves, misses included, from `esv::hit::HitResultEventOneFrameComponent`.
- Study: research/studies/attacks.md.

**The first-aim instability.** Not reproduced; explained as far as the evidence goes (study,
section 7): the failing answer shows two identical ECS reads disagreeing inside one Lua call;
`Ext.UI.Defer` callbacks run on the render thread with only the Lua lock, while the main thread
runs native ECS work; the crash below shows the main thread paused mid-dispatch while render-thread
Lua ran. Suspected bug (ours, in `Client/Aim.lua`): ECS reads from the deferred context. The fix is
the aiming executor's. Also seen: `Client/Aim.lua`'s `hook()` subscribes `OnSystemPostUpdate` from
the deferred context (the same hazard as the crash, for a subscription it never removes).

**Crash.** Once, right after the third run of the new verb: the game process ended, Script
Extender runtime log "Assertion failed: global_ != -1". Preceded by: the verb's deferred check
calling `Ext.Entity.Unsubscribe` of its three component subscriptions. Fixed by subscribing once at
load on the main thread. Restarted with `down` / `up --level GustavX:Basic_Level_A` (54 s). No crash
since.

**Lost time.** The worktree hook refused edits until the session entered the worktree
(`EnterWorktree`); the verb first waited for `IsCasting` after the click although the client creates
it at the targeting's start; two runs lost to the speed-1 fight ending at the kill (fixed with a
second goblin standing off); pins failing with "not on after 90 ticks" on three consecutive runs
right after the restart, then never again (see wishes).

**Surprises listed by the runner.** `combat_started` (began and ended inside the test) on every
fight - real and meant: allowed in `tests/combat/melee/_suite.json`. Nothing else listed.

**Wishes and questions.**
- Shared files touched, one line each: `BootstrapClient.lua` (`Ext.Require("Client/Act.lua")`),
  `Shared/EventKinds.json` (`game_hit`), `Shared/VerbClasses.json` (the `why` of `Act.Attack` and
  `Act.Cast`), contracts `docs/contracts/lua-api.md` section 19 and `lua-types/GateKit.lua`, and
  `unit/GateKit.UnitTests/VerbClassTests.cs` (its sample of a refusing name was `Attack`, which no
  longer refuses on the player's side; now `Jump`). The bootstrap line and the unit test are
  outside "Yours": accept them, or say where the client half should live.
- Taken in: `claude/poc-c332c2` merged once (coordinator's permission), for `--speed` and the new
  `Confirm` answer (`cast`), which the verb reads.
- To the aiming executor: ECS reads of Aim/Aiming/Confirm/UseSlot from the main thread (a snapshot
  written in the PickingHelper post-update hook, read by the deferred checks); `hook()` called once
  from the main thread.
- To the rolls executor: three pins in a row not in force after 90 ticks on a freshly started copy
  (Initiative, then Damage, then Attack, one per run), never again afterwards in that session.
- A test runner wish: `row(mark, kind, match)` is written by hand in each test (third time now):
  a shared `_events.lua` beside the tests (now possible with `require`).

**API review.**
- Improve: `GK.Gui.Confirm` answering the cast's start made the verb simple; the verb's guard
  counts client ticks (600) - in a deck tile the client ticks ~10 times a second, so the guard is a
  minute there and 20 s in front; a guard in game time would be steadier.
- Remove: the server's `GK.Act.Attack` / `Cast` refusals could go once nothing calls them; kept so
  that a server-side caller is told where the fair verb is.
- Cover: `GK.Act.Attack` with `who` (another party member: needs `GK.Act.Select` first, server).
- Faster: nothing in the verb polls; it waits on the game's own events.

**Commits.** `338ef78`, merge `854f6c5`, `997cdea`; merged by the coordinator as b027bab.

### Rung 1, owed: the verb's second case

**Asked.** api-rules rule 1: GK.Act.Cast tried on a second unlike case - a spell attack at range,
pinned to miss and to hit, the action paid, a refusal in the game's words.

**Done.** `tests/combat/cast/spell_attack.lua` (commit `9584035`): Fire Bolt taught (ledger), a
goblin 8 m off. A point 40 m off is refused with the cursor's own words (the target too far, the
path blocked), nothing spent; die 1 a critical miss, 0 damage; die 19 a hit for 10 with every
damage die at 10, the cursor's maximum; the action spent each time; the verb's outcome equal to the
server's `game_hit`. The pin kind `Attack` binds a spell attack too. Passes with the whole
`tests/combat` folder at `--speed 1` and `--speed working`, one session, the clean holding after
each. Uses the prelude's `row()`.

**Gaps and wishes recorded.**
- gap: the verb's guard against a hang counts client ticks (600), not game time; to change when the
  verb is next touched.
- wish: `GK.Act.Attack` / `Cast` for another party member (`who`); needs `GK.Act.Select` first.
- rule from the coordinator: no subscribe or unsubscribe from a deferred callback.

## Rung 2: the weapon after a fight (in progress)

**Asked.** Replace Clean's re-reading of the weapon with the game's own event; the study of
drawing and sheathing (state with a duration, an animation the logic waits for, or instant), what
the game raises when it is over, what an interruption does; the player's own request as a
component, used by `GK.Act.Draw` / `Sheathe`.

**Done.**
- `weapon_state` rows (Server/Act.lua, one line in EventKinds): every change of a weapon state,
  from `esv::unsheath::StateChangedEventOneFrameComponent`.
- `tests/combat/weapon/after_fight.lua`: after a scripted kill the weapon goes away in the tick the
  fight ends; after the host's fair killing blow, in the tick the attack's animation component goes
  (78 ticks at speed 1, 9 at speed 8). Passes with the whole `tests/combat` folder at both speeds,
  one session, the clean holding after each.
- Study `research/studies/weapon-in-hand.md`: the player's request is the U key, which makes
  `esv::unsheath::UserRequestComponent`; the logical change is instant (two ticks), the animation
  only requested; quick toggles do both changes in full on the server; the leaving component
  (said to be an 8 s timer) lives a few ticks and holds nothing; an attack's or a weapon button's
  animation holds the state until it ends.

**Not done, and why.**
- Clean's waiting on the weapon_state row: Server/Stage.lua is shared - proposed to the
  coordinator.
- A fair `GK.Act.Draw` / `Sheathe` through the player's request (the key): not built yet; it should
  read the key binding rather than assume U.
- Interruptions (walk, jump's aiming, cast) and the client's visual during quick toggles: not
  measured.

**Surprises listed by the runner.** `combat_started` per fight, meant; allowed in the folder's
`_suite.json`.

**Hypothesis that failed.** The leaving component as the 8 s holder after a fight (from the
coverage data's reading of the code): it lives about four ticks. A note for the coverage record.

### Rung 2, continued (a second run of this executor)

**1. Clean on the weapon's own event - the edit of Server/Stage.lua (permitted for this one place).**
Exactly what changed:
- `drawWhenSettled` (three `Ext.Entity.OnDestroy` subscriptions on CombatLeaving,
  SpellAnimationConcluded, SpellAnimationLifetime, then a re-read of the member with `member()` and
  `unsheathBusy`) is replaced by `awaitSheathed(id, redraw)`: one `Ext.Entity.OnCreateDeferred`
  subscription on the entity's `ServerUnsheathStateChangedEvent`; when its `NewState` is
  `Sheathed`, a tick later (Stage.Later) the subscription goes and, if the baseline had the weapon
  drawn, `Osi.SetWeaponUnsheathed` draws it. `Stage.Redraw[id]` now holds `{ tick, redraw, handle }`
  (was `true`); `forgetSheathing(id)` takes it off with its subscription.
- `Stage.RestoreMember`, the weapon's branch: a weapon drawn now while a fight or an unsheath
  passing component is under way is awaited (both "drawn again" and "left to the game" go through
  `awaitSheathed`); otherwise `Osi.SetWeaponUnsheathed` at once. One behaviour changed: a weapon
  drawn at the baseline and away now, with a holder under way, is drawn at once (before: after the
  holder went) - no test has a drawn baseline; untried live.
- `Stage.Verify`, the weapon's difference: `pending` only while the event is awaited and fewer than
  `WEAPON_GUARD` = 900 server ticks have passed; after that the line says the row never came and it
  is no longer pending (before: pending whenever an unsheath passing component was there out of
  combat).
- The GAP comment in Verify reworded. Contract `lua-api.md` (the drawn weapon's row) changed with it.

What is not done, and the exact proposal: the re-reading itself is the ladder's
(`src/GateKit.Scenarios/ResetLadder.cs`, `CleanAsync`, the loop over `GK.Stage.Verify` every 250 ms
while `pending`, bounded by `PendingFor` in game time) - C#, not Stage.lua, so not mine. Proposal:
`GK.Stage.Clean` returns `awaits = { { kind = "weapon_state", who, to = "Sheathed" } ... }` (and the
same for a deletion, `object_removed`, and a refill) after a cursor `since` it takes; the ladder waits
for those rows on the event log (the host's `events` stream, like the prelude's `row()`), guarded in
game time, and calls Verify once after the last. Then Verify's `pending` goes.

Proven: the redraw path ran by chance first - the copy's kept baseline had the host's weapon drawn
(left by an earlier session): after `melee/scripted_attack` the weapon went Melee -> Sheathed at tick
2405 and was drawn again at 2409, on the event; `weapon/after_fight` then found it drawn and was
unmet. Rebased by hand (`GK.Stage.Begin{fresh=true}` with the weapon away). By hand: a fight, Clean:
the line "left to the game ... its weapon_state row", the await with its subscription, then Verify
clean and the await gone. The suites: see the proof run below.

**2. Fair `GK.Act.Draw` / `Sheathe`** (Client/Act.lua). The key is read from the game's input
manager: the input event `ToggleCombatMode` (its description is the Sheathe/Unsheathe handle), its
binding in `InputScheme.InputBindings[1][EventID]` (U here). `GK.Act.Press(event)` presses the key
of any input event; the verbs answer on the client's replicated `Unsheath` component
(`Ext.Entity.OnChange`, subscribed once at load). The server's `GK.Act.Draw`/`Sheathe` now refuse
and name the UI side; the old server job is `GK.Cheat.Draw`/`Sheathe`. Two unlike cases: the ranged
set and the melee set (switched with `GK.Act.Press("ToggleWeaponSet")`). Test
`tests/combat/weapon/toggle.lua`: request at tick n, state at n+2, four times.

**Findings.**
- In a fight the toggle is not taken: no request component, no change (the host's turn). Not a
  test: an absence needs a later event inside the step.
- `ecl::unsheath::VisualStateComponent` has no replication event; `OnChange` on it fails the load
  of the file ("No replication events are available").
- A cast stuck for good: at the working speed `cast/spell_attack`'s out-of-range step saw "no
  targeting began (it shows MoveTo)"; the host's Fire Bolt cast stayed (`IsCasting`,
  `ServerSpellCastUnsheathFallbackTimer`, `SpellAnimationLifetime`) after the fight ended, and every
  clean after it failed. Escape did not end it; the copy was restarted (`down`, `up`, 36 s).
  Preceded by: a fight and a Clean I made by hand, then the speed-1 suite passing, then this run.
  Suspected: the first aim's instability at speed 8 (the aiming executor's) and a cast that waits
  for an unsheath fallback timer which never runs out - the game's or ours, not known. Not
  reproduced yet.
- The runner listed `surprise: participant <host> ... not of the party and not staged` in
  `weapon/toggle` (a test that stages nothing): noise - the host is the party.

**Wishes.**
- Clean does not take back a weapon set (`eoc::WeaponSetComponent`); the toggle test switches it
  back itself.
- No event of the game is known for the weapon set's change; the test reads it until it changes
  (a gap, in the test).
- `GK.Act.Press` belongs with the window verbs (`GK.Gui`), which are not mine.

**3. The interruption cases** - `tests/combat/weapon/interrupted.lua`, one rule, four named
cases: a second toggle 0.3 s of game time later (timed in the game with `Ext.Timer.WaitFor`, so
equally quick at both speeds), a walk (`GK.Cheat.Move`, a cheat: no fair walk), the aiming of a
jump, the aiming of an attack. The client's visual state is watched with the prelude's `expect`
(`ecl::unsheath::VisualStateComponent`, full type names: the Lua label has no layout in the probe).
Results, the same at speed 1 and at the working speed:
- the owner's hypothesis holds on the visual side: the logic goes out and back (two rows), the
  model never leaves Sheathed;
- a walk leaves the weapon drawn;
- an aim begun right after the draw creates `SpellAnimationLifetimeComponent`, which holds the
  weapon out; the toggle back after the cancel is carried out in the tick it goes.
A cast carried out (not cancelled) during the draw is not a case yet.

**The toggle test failed once.** `weapon/toggle` at speed 1 after the four other tests: the set's key
went in (`Press` ok) and the set did not change in 10 s. Not reproduced (run alone after
`after_fight`, and in the full folder at the working speed, it passed). Suspected: a key the game
does not take in some state just after a fight's clean - unexplained; kept as a suspected defect.

**A stale cursor (suspected, the runner's).** `weapon/after_fight` failed twice in full-folder runs
(once at each speed): the first matching row after a `game.mark()` was a weapon_state row written
250-270 ticks before the mark, during the previous test's clean ("away" at 37917, the fight's end
at 38185). `game.mark()` is `EventStream.Mark()`, the end of the logs as the host last read the
heads; it can lag the game. The test now also requires the rows to come after the game's tick
read beside the mark, and matches the draw by `from = "Sheathed"`. To the coordinator: a mark
should be the game's head now.

`weapon/toggle` and `weapon/interrupted` take the same tick floor now.

**The weapon set's key, not taken - intermittent, the rung's blocker.** `weapon/toggle`'s "switch
the set" failed three times in seven folder runs (twice at the working speed, once at speed 1; once
reproduced by running `tests/combat/weapon` alone at the working speed, after `interrupted`):
`GK.Act.Press("ToggleWeaponSet")` answers that F went in and the server's `WeaponSet` stays as it
was for 10 s. By hand, at speed 1 and 8, right after a draw and a sheathing, the same press switches
the set every time. Ruled out by hand: the model still drawn at the press. Not known: what state
of the game, left by the test before, makes it drop the key. Until it is known the toggle test is
not reliable, and rung 2 is not closed.

`weapon/interrupted`: the toggle back after the aim came 4 ticks after the aim's component went at
the working speed; the bound is 5 ticks, as in `toggle`.

### Rung 2, closing (after the coordinator's answers)

- Took the working branch (`git merge claude/poc-c332c2`, merge `0ac6f01`): `game.mark()` is the
  game's head. The tick floor is out of the three weapon tests; they hold on marks alone (no row of
  a previous clean came after a mark in the runs below).
- **The dropped set key, one bounded attempt.** The toggle test, on a failing run, now read the
  client's state at the failure: the controlled character's unsheath, weapon-set, spell-cast and
  movement components (nothing passing: no casting, no request pending, no animation component);
  the model Sheathed; no modifier held; no targeting held (the pointer in `MoveTo`); only HUD
  windows open; on the server the same, plus `esv::unsheath::DefaultComponent`. Reproduced at the
  working speed in a window of its own (about 30 frames a second, the deck closed), after
  `interrupted`; the earlier failures were in a tile - no side of that line. Cause not found.
  **Suspected bug** (the game's or ours, not known): `ToggleWeaponSet`'s key, injected with
  `Ext.ClientInput.InjectKeyPress`, is now and then not acted on, with nothing in the client's
  state to tell why; evidence above, 4 times in about 10 runs, never by hand.
  The set switch is out of `weapon/toggle`: the other set is put in hand by a cheat step
  (`WeaponSetComponent` written and replicated, its undo through `GK.Stage.Track`); the draw is
  still the player's key. The undo puts the set back (read after the run).
- `weapon/interrupted`: at the working speed the aim's animation component could go before the
  toggle back was pressed (the case then showed nothing). The toggle back is now pressed with the
  aim still open, and the test checks that the request came before the component went - 93 ticks
  before at speed 1, 17 at the working speed - and the change in the next tick after it.
- **The untried branch of `RestoreMember`** (a weapon drawn at the baseline, away now, a holder
  under way, now drawn at once): tried to stage it live - baseline drawn, a fight, then
  `GK.Cheat.Sheathe`: the game does not put the weapon away in a fight (the job: "the weapons are
  still out", 4 s), as it does not take the toggle there. With what has been seen, that state
  cannot be reached: a fight keeps the weapon out, and the animation components hold it out. The
  new order stays, with that reasoning; the same staging ran the branch that is reachable (drawn
  at the baseline, drawn now, the fight under way): Clean awaited the event, the weapon was put
  away and drawn again, Verify clean.
- **The proof run**, one game session, one after another, the clean holding before each test:
  `tests/combat` 6/6, `tests/_selftest` 3 passed and `steps/refusals` unmet (meant to be),
  `scenarios -k stage_clean_cycles` 1/1 - at `--speed 1`, then all three again at `--speed working`.
  Surprises listed: `combat_started` (began and ended inside the test) in `_selftest`, once per
  speed - real and meant by that test; nothing else.

**Intermittent, once each, not reproduced:** `melee/scripted_attack` at the working speed, "no
game_hit row came" after `GK.Cheat.Attack` (60 s); passes alone and in the next full run.

**Time lost.** The worktree hook (EnterWorktree again); an `expect` list written in the wrong form
(the verb as a key, not a pair); the stuck Fire Bolt and its restart; three runs of the suites
spent on a dirty kept baseline.

**API review.**
- Improve: the verbs' guard counts client ticks (600): a minute in a deck tile; game time would be
  steadier (unchanged here).
- Remove: `GK.Cheat.Draw`/`Sheathe` could go if nothing needs the order - the stage draws with
  `Osi.SetWeaponUnsheathed` itself.
- Cover: `GK.Act.Draw` for another party member (`who`), as for Attack.
- Faster: Clean's weapon now costs one subscription per member awaited, none otherwise.
- A gap left: the ladder re-reads Verify while `pending` (proposal above).

## Rung 3: the other attacks (in progress)

**Asked.** Ranged weapons (every kind as steps of one test), a spell asking for a saving throw (its
own test; the spell attack roll is `cast/spell_attack`), unarmed, off-hand; each through the fair
verb, the die pinned.

**Done.** `tests/combat/ranged/weapon_kinds.lua`: shortbow, longbow, hand, light and heavy crossbow,
each made from its root template and equipped by script, shot with `GK.Act.Attack(goblin, { spell =
"Projectile_MainHandAttack" })`, die 19, damage dice at their largest face: 9, 11, 9, 11, 13 - the
dice at most + 3 (Dexterity) for every kind, the cursor's maximum each time. Passes at speed 1 and
at the working speed, clean after.

**Bug (suspected, the shared stage's).** Clean never restores a weapon: `PARTY_SLOTS` in
Server/Stage.lua names the weapon slots `MeleeMainHand`, `MeleeOffHand`, `RangedMainHand`,
`RangedOffHand`; `Osi.GetEquippedItem` answers nil for those names and knows `Melee Main Weapon`,
`Melee Offhand Weapon`, `Ranged Main Weapon`, `Ranged Offhand Weapon`. Shown: the baseline's
`equipped` holds Breast, Boots, Underwear and the vanity slots only; after the first run of the
test the host's ranged slot was empty and Verify said only "boosts 46, were 47". Put right by hand
(`Osi.Equip` of the old bow), and the test tracks the old weapon itself. The fix is the
coordinator's: the slot names, and the off-hand ones checked the same way.

**Not done yet.** The saving-throw spell, unarmed, off-hand.

### Rung 3, continued (a third run of this executor)

**The stage's weapon slots, fixed by the coordinator - checked.** `bridge-reload`, then a fresh
baseline (`GK.Stage.Begin{fresh=true}`) with the host armed (dagger, shortbow): its `equipped` now
holds `Melee Main Weapon` and `Ranged Main Weapon`. `weapon_kinds` passed at speed 1 with its own
restoring still in; the old bow was back after the clean. With the restoring taken out it passed at
the working speed and the clean put the old bow back by itself (`1c45b93`). The clean and the test's
undo did not fight: both equip the same item, and the run with both passed and left the bow in
hand, Verify clean. `weapon/toggle`
passed at both speeds. What the clean does wrong instead (found in the unarmed test): see below.

**1. A spell's saving throw** - `tests/combat/cast/saving_throw.lua` (`93175b0`). Sacred Flame at
four goblins, the target's `SavingThrow` pinned (`who` = the target), the host's DC shifted by the
game's `SpellSaveDC(n)` boost through the ledger. DC 11: a 1 (total 2) fails, 8 radiant; a 20 (21)
saves, nothing. DC 26: a 20 fails. DC -4: a 1 saves. So the wiki's rule holds: a spell's save is
not decided by a natural 1 or 20. The case the brief foresaw (a save pinned at 20 that cannot reach
its DC) came up and fails, as the wiki says; no conflict with the pin. The save is in the hit
(`HitDesc.ConditionRolls`), and `game_hit` rows now carry `saves` (Server/Act.lua, one line of
EventKinds, the contract). Tried on a second, unlike case by hand: Acid Splash through
`GK.Cheat.Cast` at two goblins, one save per target in its own row.
- **To the rolls executor (a to-do and a finding):** Osiris `RollResult` fires for a saved spell
  save only (four saved with a row, four failed with none), and the `game_roll` row it makes reads
  "passive check succeeds", with no kind, DC or die: the spell's save passes through no `esv::roll`
  finished event to join. A natural 20 that fails still has `Critical = Success` in the hit's roll.

**2. The unarmed strike** - `tests/combat/unarmed/strike.lua` (`e1eea01`). The main-hand weapon
taken off; `Target_UnarmedAttack` (the game's, `Guaranteed`); Strength shifted by `Ability(Strength,
n)` through the ledger. Strength 8 (-1) and 2 (-4): 1 each, not the wiki's 0 and -3; Strength 14
(+2): 3; a critical at 14: 3 (no dice to double). Hit chance 30/45/60 %: Strength in the roll, 5 % a
point. Not settled: a floor at 1 or a negative modifier left out.
- **Bug (suspected, the shared stage's - for the coordinator):** `RestoreMember` puts spells back
  before equipment, and `Osi.Equip`/`Unequip` land some ticks after the call. After the weapon was
  off, Clean added `Target_MainHandAttack` by `Osi.AddSpell` (source `Osiris`) beside the one the
  re-equipped weapon brings; Verify (spells by name) said clean; the copy kept the game from giving
  the unarmed strike, so the second run of the test failed at "the host has the unarmed strike".
  Shown live: the copy present after the first run's clean; removed by hand, the strike came back at
  once. A `Projectile_MainHandAttack` copy was there too (from earlier runs). Proposal in the study,
  section 11: equip first and diff the spells when the equipment is back, or leave item-granted
  spells out of the diff. Meanwhile the unarmed test equips the weapon again in a last cheat step
  (written there, with the reason), and the clean finds nothing to do.
- My own damage while cleaning by hand: removing the Osiris copy of `Projectile_MainHandAttack`
  took the bow's hotbar slot with it; `weapon_kinds` then failed ("no slot of HotBar holds
  Projectile_MainHandAttack"); unequipping and equipping the bow put the slot back. The hotbar is not
  in Clean's baseline - a to-do of the stage if a test ever changes it.

**3. The off-hand attack** - `tests/combat/offhand/light_weapons.lua` (`4488525`). A dagger, then
a handaxe in the off hand beside the host's dagger. With the toggle off: main hand 7 (1d4 at 4 + Dex
3), action spent, bonus kept; off-hand dagger 4 (no Dex), off-hand handaxe 5 (1d6 at 6 and Strength
-1 kept), bonus spent. With the toggle on: one main-hand attack makes both hits (7 and 3, both named
`Target_MainHandAttack` in the hit) and spends both resources. Finding against the wiki's sentence:
a negative modifier is kept on the off hand.
- The toggle is set by a cheat step (`eoc::DualWieldingComponent.MeleeToggledOn` written and
  replicated, written back by the test's own track). The fair way is not reached:
  `GK.Gui.Click("HotBar", "DualWieldToggle")` answers "no command DataContext.ToggleDualWieldingCommand"
  (the button's context is an `ls.VMInputEvent`); `GK.Act.Press("ToggleDualWielding")` presses its
  bound RETURN and nothing changes; executing the button's own `Command` by hand toggled it. **Wish**
  (Windows.lua is not mine): Click falls back to the control's own command. Equipping an off-hand
  weapon turns the toggle on again.

**The proof runs.**
- Session A (the copy as handed over): `tests/combat` at speed 1, 9 of 10 - `weapon/interrupted`
  failed at "a second toggle": both presses of `ToggleCombatMode` went in and no `weapon_state` row
  came (the dropped key again, now the combat-mode key; the clean before it had taken back a
  `combat_started`). Then at the working speed `melee/attack_by_die` failed at its first aim ("the
  game's targeting did not take the aim (it holds nothing)", pointer in `MoveTo`) and **a cast stuck
  for good**; every clean after it failed. What it held, read before the restart: the server's host
  had `IsCasting` with a cast entity whose `SpellCastState` was `Target_MainHandAttack`, options
  `ShowPrepareAnimation|FromClient|IsPreview`, no targets; the entity carried the server's cast
  components (state, cache, interrupt, `ServerSpellCastUnsheathFallbackTimer`, client-initiated,
  hit register, rolls, sync targeting ...); the host also had `SpellAnimationLifetime` of that cast
  (`field_4C` 5.0); the weapon drawn; not in a fight. The client had no cast at all, the pointer in
  `MoveTo`. Verify named `IsCastingComponent` and `SpellAnimationLifetimeComponent` as "the game
  still at work". A fresh `UseSlot` + `CancelAim` on the client did not end it. So: an orphaned
  *preview* cast of the server, its client side gone - the same shape as the stuck Fire Bolt of rung
  2 (both at the working speed, both after an aim the targeting did not take). Not tried, for next
  time: `GK.Cheat.Unstick`. Restarted (`down`, `up --level`, 36 s).
- Session B (fresh process, `bridge-reload`, the baseline taken from the world as found): speed 1,
  9 of 10 - the three new tests passed; `weapon/toggle` was not run: the clean after
  `weapon/interrupted` stayed dirty ("esv::unsheath::DefaultComponent ... field_0 = 3, its default
  1" - the shared stage's Verify, for the coordinator). Working speed: 10 of 10.
- So the rung's three new tests pass at both speeds in one session; the folder as a whole does not
  yet, for the two intermittent defects above (both weapon-toggle related, neither in a new test).
- Surprises listed: `combat_started` / `combat_ended` taken back as side effects of the test before,
  each run (real, meant: every test fights; allowed in each folder's `_suite.json`); `job_done`
  after `scripted_attack` (real: its scripted attack's job). Nothing else.
- `dotnet test`: one failure, `RollsTests.Once_takes_the_pin_off_after_the_holders_next_finished_roll_of_the_kind_only`
  ("attempt to index a nil value"). It loads Rolls/Stage/Kits/EventLog and EventKinds.json only;
  my one change there is a payload description of `game_hit`. Taken as the rolls executor's, not
  verified at the base commit (a worktree of it would not build: the submodules are missing there).

**Where rung 3 stands** (the owner's change of course stops widening here): ranged weapons, the
spell attack roll, the saving-throw spell, unarmed, off-hand - each a test, each passing at both
speeds. Left: the off-hand attack before any main-hand attack (a refusal, not a case yet), the
toggle the player's way (a Click wish). Rung 4 (fair movement) and rung 5 (the opportunity
attack): not started, put off by the coordinator.

**The coordinator's notice on `Confirm`** (a throw's first click answers `picked`): no throw in my
rungs; the walk a cast makes out of a fight when its target is out of reach is noted for rung 4.

## The instruments: a descent into the attack (the owner's change of course)

**Asked.** Every instrument tried on one fair attack, each result written as it comes: the
tracer, the memory instrument (`Field`, `Watch` + `Hits`, `Freeze`), `decomp at` / `code`,
`symbols`, `names set` / `names extender`; toward where the d20 of an attack is drawn and compared,
whether the natural die is kept, where a pin's minimum and maximum enter. Scene for all of it, by
hand through `gatekit exec` in copy `attacks` (three other copies running): a stage scope, the
host's initiative pinned, a hostile goblin in reach and one standing off, a fight, the host's
attack die at 19 and damage at 4; the attack `GK.Act.Attack(goblin)` from the UI side; the scene
cleaned and verified after (Verify clean once the pins' boosts had left, a few seconds).

**1. The tracer - works at the first call.** `Ext.Entity.SetupTracing{ TrackECB, TrackImmediateWorldCache,
TrackReplication, TrackModifications = true }`, `EnableTracing(true)`, `ClearTrace()`; 3 s idle
in the fight as a baseline (131 component names); `ClearTrace`, the attack, `GetTrace()`. The
timeline, in ECS frames after the clear (each row a component, Create/Destroy/Replicate/OneFrame/
Modify read from the change's own booleans): +25 the server's cast entity and all its
`esv::spell_cast` / `eoc::spell_cast` components (state, cache, rolls, sync targeting, client
initiated, unsheath fallback timer) - the preview from the client's targeting; +26 the animation
request, `SpellAnimationLifetime`, the weapon set request; +27 the weapon drawn
(`eoc::unsheath::StateComponent`); +47 `PreviewEndEvent`, `TargetsChangedEvent`, movement - the
click; +48 the confirm notification; +50 `LogicExecutionStartEvent`; +71 `CastEvent`,
`CastHitEvent`, `TargetHitEvent`, `HealthComponent` modified, `EntityDamagedEvent`, a status (the
blood); +72 the hit entities (`eoc::hit::AttackerComponent`, `TargetComponent`, `WeaponComponent`,
`MetaComponent`, `LifetimeComponent`), `HitResultEvent`, `ActionResourcesComponent` modified;
+82 `FinishedEvent`, `OutcomeComponent`; +83 `DestroyEvent`. The same order as the study's timeline
(section 4). What the stamps give: `Frame` and `Flush` advanced together here (one ECB flush per
frame, both 35163 for the first row), so they order frames, not changes inside one. What was wrong:
the entity of a change could not be named - `el.Entity` (and the map's key) did not resolve with
`Ext.Entity.Get`/`.Uuid` in my chunk, so the baseline could only be subtracted by component name,
not per entity; `Flags` cannot be read (known). Time: two calls, under a minute.

**2. The memory instrument - built and working, in-game (`GK.Memory`, Server/Memory.lua over the
fork's `Ext.Debug`).** The documents disagree; the true one is docs/writing-tests.md: "who writes
this field" is built and needs no external debugger.
- `GK.Memory.Field(goblin, "eoc::HealthComponent", "Hp")` -> `{ Address, Offset = 0, Type = int32,
  Size = 4 }`; `GK.Memory.Read(f)` = 2 = `Health.Hp`. First call.
- `GK.Memory.Watch(f)` -> slot 1; the attack (7 damage on 2 hit points); `GK.Memory.Hits()` -> 3
  hits: frame 72, `bg3_dx11.exe+0x31C2DAE`, after 0 (the hit points written to 0); frame 73, twice
  `VCRUNTIME140.dll+0x1C2DF`/`+0x1C2EA` (a memory copy called from `bg3_dx11.exe+0x4024D49`) writing
  9: the goblin died and its component storage moved, another creature's health copied into the
  watched address. Two lessons: a watch on a component's address holds only until the storage
  moves (a death does it); `Before` is not the value before the write (0 -> 0 for the 2 -> 0
  write) - a data breakpoint traps after the write; **suspected bug of ours** (Memory.lua or the
  fork): `Before` is wrong, or should be left out. Each hit carries its return stack, as module+RVA.
- `GK.Memory.Freeze(f.Address, "int32", 5)` on the other goblin's Hp, then `Health.Hp = 7` from Lua:
  both reads 5 a second later; `Unfreeze()` -> nothing held. Works.
- `GK.Memory.At(address)` of a heap address answers nil, as documented.

**3. decomp and symbols - work.** `gatekit decomp at bg3_dx11.exe+0x31C2DAE` -> inside
`FUN_1431c2c10` (+0x31C2C10), no symbol; `+0x31C34A5` (the return) -> inside
`esv::StatsSystem::ThrowDamageEvent` (+0x31C32F0); `+0x4024D49` -> inside `FUN_144024ca0`. Each
under 5 s. `decomp code FUN_1431c2c10`: my sentence - it takes the target's `eoc::HealthComponent`
through `ecs::GetComponent`, computes the new health from a change it is given and writes it,
clamping at 0; it is the server's apply-a-health-change step that the damage event calls.
Toward the d20: `symbols find AttackRoll` / `RollDice` names the roll code in the Toolkit's
**EoCShared.dll** - `eoc::roll::stream::RollManager::InternalRollDiceWithBoosts`
(EoCShared.dll+0x1203EA0; its signature takes a `WorldView` of `eoc::MaximumRollResultBoostComponent`
- where a pin's maximum enters, in the editor's build), `RollDice` (+0x1204DE0), `RollDiceAgainstDC`
(+0x1204F50), `eoc::stats::GetWeaponAttackRoll` (+0x116F0C0), `GetAttackRollResults` (+0x1169CA0).
None of them is found again in the game: `symbols game <name>` and `decomp matches <name>` (also
`--all`) answer nothing for each. `decomp helpers eoc::MaximumRollResultBoostComponent` gives the
game's getters (`bg3_dx11.exe+0xAE6C40`, `+0xB2FE60`, `+0xCA0D60`); `symbols callers` of them give
unnamed functions (`+0xA26790`, `+0xA26900`, `+0xA26500`, `+0xA266D0`, `+0x105AD10` ...). Not walked
yet: that static walk is long, and a live reading is shorter - **next**: a read watch
(`GK.Memory.Watch(addr, 2, true)`) on the host's `MaximumRollResultBoostComponent` (the pin's
boost) during an attack names the instruction that reads it, which is inside the game's
`InternalRollDiceWithBoosts`; from there `decomp code` up to the comparison with the armour class
and to where the result is kept.

**4. Names.** `gatekit names set` names *fields* of components only: there is no command to name a
function (`decomp`, `symbols` and `names` have none) - a gap in the toolkit for the machine-code
work; `FUN_1431c2c10` stays unnamed. Set with evidence: `eoc::HealthComponent+0x0 Hp`, rung extender
-> inferred, the evidence the watch, its write at frame of the damage, the Freeze
(research/coverage/layouts/extender/eoc.HealthComponent.json; the record is shared - the coordinator
asked for `names set`). `names extender` lists 172 fields elsewhere and nothing of what I touched.
Script Extender's declarations checked against what was read: `HitDesc.ConditionRolls`
(`Difficulty`, `Ability`, `Roll.Result.NaturalRoll`/`Total`/`Critical`) read values that agree with
the game (DC 11 with the cursor's 45 %, totals = die + 1 for a Dexterity +1 goblin);
`DamageFlags.SavingThrow` set exactly on the saved hits; `eoc::DualWieldingComponent.MeleeToggledOn`
written and followed by the game; `HealthComponent.Hp` at +0x0 confirmed by the watch. None found
wrong.

**5. The questions, so far.** Where the d20 is drawn and compared: not yet (above, why, and the next
call). Whether the natural die is kept readable: for a **saving throw**, yes - in the hit
(`HitDesc.ConditionRolls[].Roll.Result.NaturalRoll`), read through Script Extender already, now in
`game_hit.saves`; for an **attack roll**, not found in the hit or in the cast's `RollsComponent`
yet (the next reading). Where a pin enters: in the editor's build, `InternalRollDiceWithBoosts`
reads the minimum/maximum roll-result boost components (the rolls executor's question; its code not
met yet). Several copies running: nothing misbehaved - every call answered, the tracer and the
watch saw only this copy's game (the watch is per process).

### The d20: from the pin's boost to the code

**The read watch crashed the game.** Scene as above, the host's attack pinned at 19; the two boost
entities' `eoc::MinimumRollResultBoostComponent` / `MaximumRollResultBoostComponent` found through
the host's `BoostsContainer` (`GK.Memory.Field(..., "Result")` = +0x1 of a 2-byte component, read 19);
`GK.Memory.Watch(componentAddress, 2, true)` on both (slots 1, 2); then `GK.Act.Attack(goblin)`: the
game process ended within seconds, nothing in Script Extender's log, no dump found. Cause, read in
the fork (Lua/Libs/Debug.inl, `WatchHandler`): the vectored handler reads the watched address
itself (`SafeReadScalar`) to record the value - with a read watch armed that read traps again
inside the handler, without end. **Bug (ours, the fork's, confirmed by reading; one crash as
evidence)**: a read watch cannot work as built. Wish for the fork: clear the slot's DR7 bits in
the trapped thread's context before reading, or skip the read for a read watch. The same code
explains `Before`: it is the value at the slot's previous hit, and the first hit has none (the arm
records no value), so it equals `After`. Restarted (`down`, `up`, 48 s).

**The way round: freeze instead of watch.** `GK.Memory.Freeze` of both `Result` bytes from 19 to 2,
then a fair melee attack (armour class 13, bonus +5): a plain miss (`Dodge|Miss`, not the critical
miss of a 1), where the pin at 19 had hit. So the attack's die is the boost's `Result`, read at
roll time. Named with that evidence: `eoc::MaximumRollResultBoostComponent+0x1 Result`,
`eoc::MinimumRollResultBoostComponent+0x1 Result` (extender -> inferred).

**Into the code, statically, meeting the rolls executor's chain.** `decomp helpers
eoc::MaximumRollResultBoostComponent` gave the game's getters; `symbols callers` of the getter
`+0xB2FE60` gave `+0x105AD10`, whose only caller is `+0x105CCA0` - a link of the rolls executor's
chain (`+0x105AAB0` <- `+0x105C9B0` <- `+0x105CCA0` <- `+0x105B800`): **our two descents meet
there.** My sentences, from `decomp code`:
- `+0x105AD10`: given a roll type and one drawn die, walks the roller's boosts and lowers the die to
  each matching `MaximumRollResult` boost's `Result`, then raises it to each matching
  `MinimumRollResult`; a boost of the umbrella type (`RollType` 1, Attack) matches every attack
  roll through a helper - which is why a pin of kind `Attack` binds a spell attack too.
- `+0x105CCA0`: draws the dice through `+0x105C9B0`, then passes every drawn die through
  `+0x105AD10` and writes the bounded value over the drawn one in place.
- So the natural die *as drawn* is not kept in that buffer once a boost bounds it; what goes on is
  the bounded die. Not yet found: where that die is added to the bonus and compared with the armour
  class (above `+0x105B800` / `+0x1075BA0` / `+0x11888B0`), and whether the outcome's record keeps
  the die - the fork wish waits on that.

**Names** (`gatekit decomp name`, merged in at 5e47607; the ledger research/coverage/functions.jsonl):
- `bg3_dx11.exe+0x31C2C10 ApplyHealthChange` - the Hp write caught by the watch at `+0x31C2DAE`.
- `bg3_dx11.exe+0x105AD10 ApplyRollResultBoostsToDie` - the freeze, and its reads of exactly those
  two components.
- `bg3_dx11.exe+0x105CCA0 RollDiceWithBoosts` - its calls of the die draw and of the above; the
  Toolkit's counterpart `eoc::roll::stream::RollManager::InternalRollDiceWithBoosts`
  (EoCShared.dll+0x1203EA0) takes the same component.
`decomp at` shows each name, its `does` and its evidence.

**Per tool, this hour:** `Memory.Field` and `Freeze` - right at the first call; `Watch` with reads -
crashed the game (above); `decomp helpers`, `symbols callers`, `decomp code`, `decomp name` - worked,
seconds each; `decomp matches` / `symbols game` - no pair from the Toolkit's roll functions to the
game. Other copies running: no effect seen.

## The descent, a fourth run: the comparison, the kept die, the damage dice

Copy `attacks` started fresh (`up --level GustavX:Basic_Level_A`, playable after 30 s). Scene by
hand through `exec server`: `GK.Stage.Begin()`, pins Initiative 100, Damage 1, Attack 15; two hostile
goblins (one beside the host, 9 hp, one 6 m off), `GK.Stage.Fight`. Between attacks the turns are
ended by `GK.Cheat.EndTurnOf` until the host's action is back. Damage pinned at 1 so that the goblin
survives two hits (dagger 1d4 at 1, + 3).

**The fair verb, first call: a wrong answer, a real attack.** `exec ui "return
GK.Act.Attack('<goblin>')"` answered `{ ok = false, did = "clicked and left the targeting", saw = "the
first click of Target_MainHandAttack picked <goblin>: the targeting waits for the second (GK.Gui.Aim
where it goes, then GK.Gui.Confirm)", ticks = 26, waitedMs = 838 }` - expected `ok` with the outcome.
But the attack was carried out: the goblin 9 -> 5 hp, the host's action spent, the PlayerSpell and
PlayerDamage roll streams created with dice drawn from them. Told to the coordinator at once (the
`picked` sign of `Confirm` fires on a one-click attack; the aiming executor fixes `Client/Aim.lua`).
From then on by hand: `GK.Gui.UseSlot('Target_MainHandAttack')`, `GK.Gui.Aim(goblin)`,
`GK.Gui.Confirm()` - each answered at the first call (`Confirm`: `did = "clicked"`). A first try
of `UseSlot` + `Aim` after the verb's attack answered `valid = false`, "not enough resources" - the
action already spent, which is how the attack above was noticed.

**A watch on the roll streams - where the attack's d20 is drawn from.**
- `Ext.Debug.GetSystemAddress("esv::roll::stream::SaveSystem")` now answers a table `{ Address,
  Index, Activated, UpdateProc }`, not a number (the rolls executor's chunk used it as a number):
  arithmetic on it failed at the first call; `.Address` works.
- The manager (system +0x18) had ONE stream (Generic, count 3) before the first attack and FOUR
  after it (Generic, PlayerSpell, PlayerDamage of the host; NPCDamage of the fight): a stream is made
  at its first use, and the stream buffer (manager +0x8) moved when they were made (0x...8840 ->
  0x...8800 -> 0x...1040 later) - so the first watch, armed on the Generic stream before the attack,
  saw nothing of it. Re-armed on each of the four streams' `NumRolls` (buffer + i*0x78 + 0x70).
- A watch on the manager's stream count (+0x14): written every frame (4 -> 0, 0 -> 4) by
  `SystemUpdate<esv::roll::stream::UpdateSystem>` (+0x238ABC3) - the manager's array is emptied and
  refilled every frame from the component, in place (capacity 4 kept). Noise, cleared.
- Every frame each stream's count is rewritten with its own value at +0x105DBBD (the copy, as the
  rolls executor found): 4 x ~128 hits in 4 s, and the ring holds 512 - the noise nearly fills it.
  A summary in Lua grouped the hits by instruction and return chain (kept in the server's state for
  the session, not in the tree).
- **One fair attack pinned at 2 (a plain miss, the goblin kept its 5 hp, the action spent):** two
  hits on the PlayerSpell stream (2 -> 3, 3 -> 4) in one frame at +0x105AAE3 (the draw), return chain
  +0x105CB3D (`RollDice`) <- +0x105CDA1 (`RollDiceWithBoosts`) <- **+0x105B9ED (in +0x105B800)** <-
  **+0x106CE8B (in +0x106CA40)** <- +0x106FE82 (in +0x106FB80); and 20 frames later one hit on the
  PlayerDamage stream through +0x10713AF (in +0x1070DE0) <- +0x36E9347 (in +0x36E8A80) <- +0x36EC3FC
  (in +0x36EBCB0) - a damage die drawn on a miss.
- **One fair attack pinned at 15 (a hit for 4, the goblin 5 -> 1):** the same two d20 draws through
  the same chain; then two draws on PlayerDamage in one frame, both through +0x10713AF <- +0x36E9347,
  one returning to +0x36EC3FC (in +0x36EBCB0), one to +0x36EA93B (in
  `stats::DealDamageFunctor::ApplyDamage`, +0x36E9F60, a name the symbols already have).
- The return chain is six deep at most (asked for 16): the chain stops at +0x106FE82, and the
  function above it is found with `symbols callers` (+0x106FB80 <- +0x116B430, +0x32955C0 (fuzzy
  `esv::RollSystem::RollDice`), +0x41D4950).

**Read upward with `decomp code` (my sentences; nothing of the code kept):**
- +0x105B800: finds, in the roll manager's streams (0x78 bytes each), the stream the roll asks for,
  marks the manager changed (+0x2A); when weighted rolls are on (manager +0x28) and the stream's type
  is one Karmic Dice weights, it rolls through the weighted path (+0x105C5A0); otherwise through
  `RollDiceWithBoosts`. So it is the roll manager's "roll on a stream" for every roll kind: its
  callers are the attack (+0x106CA40), `eoc::stats::AbilityCheck` (+0x10764A0) and five others.
- +0x1075BA0 (named in the brief): a check against a difficulty - it takes a skill (`GetSkillAbility`
  of its argument) and sets success where the total reaches the DC. Not the attack's: the attack's
  draws do not pass it.
- **+0x106CA40 - the attack roll against the armour class, and where the outcome is written.** It
  takes the target's armour class from `eoc::ResistancesComponent.AC`, rolls through +0x105B800 into
  a roll result (total, natural die, critical state: 1 a natural 20, 2 a natural 1); a
  `GuaranteedChanceRollOutcome` boost on the attacker replaces the result by one drawn with the C
  runtime's `rand()` on the side it guarantees; a `DodgeAttackRoll` boost of the target can turn it
  into a natural-1 miss; it hands the roll result and the armour class to +0x1067D50, which decides
  the hit; then it writes the outcome into the hit's `HitDesc.EffectFlags` (+0x2C, the offset Script
  Extender declares; values from `Ext.Enums.DamageFlags`): Hit (1) for a hit, Dodge|Miss (2|4) for
  a miss, Critical (8) for a critical, AttackAdvantage or AttackDisadvantage (0x2000/0x4000) when only
  one of them holds - and **appends a ConditionRolls element (`HitDesc` +0x50) with that roll result
  and the armour class at the element's +0x118**. Its result is whether it hit.
- **+0x1067D50 - the comparison with the armour class.** It gathers critical overrides (a forced
  critical hit or miss, a critical cancelled; the distance between the two and their heights enter),
  then answers a miss for a natural 1, and for a total below the armour class unless a natural 20;
  a hit otherwise. Where the die meets its bonus is one step lower, in `RollDiceWithBoosts`
  (+0x105CCA0, the rolls executor's name): it sums the kept dice and the formula's bonus into the
  total and fills the roll result - total, natural die (the total less the bonus), the other die of
  the pair, critical (2 for a natural 1, 1 for a die at or above the critical threshold) - and moves
  the Karmic debts by comparing that total with the difficulty it is given (here the AC).
- **The freeze that backs it**: `GK.Memory.Field(goblin, "eoc::ResistancesComponent", "AC")` ->
  +0x10 int32, read 13 = the Lua value; `GK.Memory.Freeze(address, "int32", 21)`, Lua read 21 a
  second later; a fair attack with the die still at 15 (total 20): `Dodge|Miss`, 0 damage, and the
  hit's `ConditionRolls[1].Difficulty` read 21. AC written back to 13 by Lua after `Unfreeze` (a
  freeze leaves the last value in place).
- **A write watch on the hit's flags** (the brief's first idea) cannot be armed ahead: the record is
  made inside the attack, on the stack of the hit's resolution, and its address is not known before;
  the stream watch was the way in, and the hit's record was read after through Script Extender.
- +0x1070DE0: rolls a damage formula through `RollDiceWithBoosts` on the damage stream - called by
  +0x36E8A80, which `stats::DealDamageFunctor::ApplyDamage` and +0x36EBCB0 call; also by
  `esv::AiHelpers::CalculateAiScoreStatsFunctors`.

**The natural die of an attack is kept - in the hit, as for a saving throw.** A probe subscription
(`Ext.Entity.OnCreateDeferred("HitResultEvent")`, made from `exec`, main thread) over the hit pinned
at 15: `HitDesc.ConditionRolls` has one element, `Roll.Roll.RollType = MeleeWeaponAttack`,
`Difficulty = 13` (the goblin's armour class), `Roll.Result.NaturalRoll = 15`, `Total = 20`,
`Critical = None`, `DiscardedDiceTotal = 15`; flags `Hit|HitpointsDamaged`, damage 4. So the long-open
item closes: the attack roll's natural die is readable through Script Extender today, in the
hit's `ConditionRolls` - which `game_hit` drops, because `savesOf` keeps only `SavingThrow` rolls.
No fork wish is needed. Caveat, from `RollDiceWithBoosts`: "natural" is the die after the pin's
boosts bounded it (written over the drawn one in place), not the die the stream drew - the stream's
own draw is gone. The discarded die of the pair is clamped too (15).
- Proposed (Server/Act.lua is mine, done below if time allows): `game_hit` gains `attack = { kind,
  ac, natural, total, critical }` from the hit's attack-roll element, as `saves` does for saves.

**The damage dice (task 3), one reading.** The watch on the host's PlayerDamage stream count does
hit +0x105AAE3 during a hit: twice for a dagger hit (1d4 at the pin's 1, 4 damage), both through
+0x1070DE0 <- +0x36E8A80, one from `stats::DealDamageFunctor::ApplyDamage` and one from +0x36EBCB0;
and once on a miss, through +0x36EBCB0 only. So the damage dice are drawn on the damage stream by
the same draw, through `RollDiceWithBoosts`, once per die per call - and one call (from +0x36EBCB0)
runs whether or not the attack hits; what it is for is not read (not yet: a weapon of two dice would
tell whether it is one draw per die).

**Took the working branch** (merge `83aa2fe`, build clean, `bridge-reload` 1.6 s). Found after: the
reload forgot the open stage scope in that Lua state - the next `GK.Rolls.Pin` was refused ("no
stage scope is open") while the earlier pins' boosts stayed on the host and still bound the die (the
attack after it came 15). Known shape (a pin of a previous Lua state, decision 51); written down as
what a reload does mid-scene.

**Names given (each with its reading; `decomp names` lists them):**
- `bg3_dx11.exe+0x106CA40 RollAttackAgainstArmourClass` - the watch's chain, the hit's ConditionRolls
  and the AC freeze.
- `bg3_dx11.exe+0x1067D50 DecideAttackHit` - called by the above between the roll and the flags;
  its rule matched the three attacks (5 vs 13 miss, 20 vs 13 hit, 20 vs 21 miss).
- `bg3_dx11.exe+0x105B800 esv::roll::stream::RollOnStream` - the watch's chain (+0x105B9ED).
- `bg3_dx11.exe+0x1070DE0 RollDamageDice` - the watch on the damage stream (+0x10713AF).
- Variables of RollAttackAgainstArmourClass (`decomp vars`): `hit`, `hitDecided`, `rollResult`,
  `rollCritical`, `conditionRoll` (`--struct conditionRoll=eoc::ConditionRoll`).
- Structure fields (`decomp field`, source extender, our reading as evidence): `eoc::HitDesc+0x2C
  EffectFlags`, `+0x50 ConditionRolls`, `eoc::ConditionRoll+0x118 Difficulty`.
- Component field (`names set`): `eoc::ResistancesComponent+0x10 AC` - the freeze.

**The new tools, first calls:**
- `decomp vars` - worked at the first call; the names show at once in `decomp code`, the signature
  carries the function's name. A defect of the method rather than the tool: the decompiler reuses
  a variable for two meanings (`local_4d8` holds an argument and later the armour class, `iVar25`
  the armour class and later a die), so such a variable cannot be named for one; I took those two
  names off again (`iVar25=` - the old name, as the help says; my first try `armourClass=` with the
  new name did nothing and said nothing). Wish: `old=` with a name that is no variable of the
  function is refused with the reason.
- `decomp field` - worked at the first call; `--struct` on `decomp vars` lists the field above the
  code. 
- `decomp name` shows the callees' names inside the caller at once (`RollOnStream(...)`,
  `DecideAttackHit(...)` inside RollAttackAgainstArmourClass).
- `GK.Memory.System(name)` - worked; it answers the fork's table `{ Address, Index, Activated,
  UpdateProc }`, where its comment says "the address" (a small defect of the comment or the verb).
- `GK.Memory.Element(bufferAt, index, 0x78, 0x70)` - worked at the first call (the four streams'
  counts).
- The read-watch refusal and `Moved`: not exercised (I armed no read watch; no watched storage
  moved: the stream buffer is the manager's, not a component's).
- Field names in the code (merge `e65f529`): `decomp vars <RollAttackAgainstArmourClass> lVar3=hitDesc
  --struct hitDesc=eoc::HitDesc`, with `eoc::HitDesc+0x4C HitWith` named (`decomp field`, extender,
  read None on a live miss): the line in the early return printed `hitDesc->HitWith /*+0x4C*/ =
  ...` - **reads true** there (the hit record taken from the caller's context, its HitWith byte).
  But the decompiler reuses that variable later for two unrelated buffers (lines that free them),
  so the name `hitDesc` was false there - no field was substituted in those lines (no named offset),
  only the variable's name misled. Taken off again (`lVar3=`, then `--struct hitDesc=`; after the
  first the structure's binding stayed under the dropped name, listed in the legend, until the
  second). The flags' writes (`+0x2C`) go through the caller's pointer (`param_12[0xd]`), not a
  variable, so they were not substituted. Wish: a variable's name scoped to a range of lines, or a
  warning when a named variable is assigned twice.

**`game_hit` carries the attack roll** (Server/Act.lua `attackOf`, one line of EventKinds, the
contract section 19's paragraph): after `bridge-reload`, a fair attack pinned at 2 wrote
`attack = { kind = "MeleeWeaponAttack", ac = 13, natural = 2, total = 7 }`, outcome miss,
`Dodge|Miss`. Not yet run under `gatekit test` (the folder's tests are not tonight's).

**The tracer over one fair attack, with its real options (task 4).** Read in the fork: the options
are `TrackECB`, `TrackImmediateWorldCache`, `TrackReplication` (default on), `TrackModifications`
(default off) and `ExcludeModificationComponents` (a list of component names whose modifications
are not logged); a change is `{ Name, Type, Frame, Flush, Flags }` under the entity's log
`{ Components, Entity, Flags }`, keyed by the entity. What came out:
- `SetupTracing{ ..., TrackModifications = true, ExcludeModificationComponents =
  {"esv::roll::stream::StreamsComponent"} }` - accepted; the streams' per-frame modification did not
  appear.
- **The entity of a change: yes, through the key** of `GetTrace().Entities`. The log's own `Entity`
  is nil - the fork never writes it (`ECSChangeLog::AddComponentChange` fills the map's key only:
  EntitySystem.cpp) - which is why the first try failed. The key is an entity: `Ext.Entity.Get(key)`
  answers it and `Ext.Utils.HandleToInteger(key)` compares with the host's and the goblin's. Over
  the attack: the host's `IsCasting` and weapon-set request, the cast entity's components, the
  goblin's `HitAnimationRequest`, `hit::Reaction`, `UnresolvedHitNotification`, and three new hit
  entities (`Meta`, `Lifetime`, `Attacker`/`Target`/`Weapon`) - each named by entity.
- **The order inside a frame: no.** `Flush` was `Frame + 1` for every change (relative to the
  clear): one ECB flush per frame, so the two stamps order frames only. Also from the code: a
  component's `Frame` is its FIRST change since the clear (set only while its flags are 0), so a
  component changed every frame shows only its first frame - why a per-frame component hides its
  later change.
- `Flags` (of a change and of the entity): "Property does not exist" in the running build
  (v34-tools), though the fork's property map declares them (`P_BITMASK(Flags)` in
  CommonTypes.inl). Not explained: the generated map has them too.
- The attack's timeline, frames after the clear: +23 the cast entity and its components (the
  targeting's preview) and the host's `IsCasting`; +24 animation request and the weapon set
  request; +65 preview end, targets changed; +68 logic execution start; +89 `CastEvent`,
  `TargetHitEvent`; +90 the hit entities and `HitResultEvent`; +91/+92 the hit notifications;
  +100 `FinishedEvent`, `OutcomeComponent`; +101 `DestroyEvent` - the predecessor's order, now with
  the entity of each row.
- Wishes for the fork (not built): `entry->Entity = entity` in `AddComponentChange`; a running
  sequence number per change (the order inside a frame); `Flags` readable.

**Script Extender's declarations against what was read (task 5).** `gatekit names extender` lists
nothing of what I touched (its rows are other components'). Checked by hand:
- `HitDesc`: `EffectFlags` at +0x2C, `HitWith` at +0x4C, `ConditionRolls` at +0x50 (offsets
  computed from Hit.h) - the code writes exactly there; `DamageFlags` values Hit 1, Dodge 2, Miss 4,
  Critical 8, AttackAdvantage 0x2000, AttackDisadvantage 0x4000 - the code's ors.
- `ConditionRoll.Difficulty` - written by the attack at the element's +0x118, read through the
  declaration as 13 and 21 (the frozen AC).
- `StatsRollResult` (`Total, NaturalRoll, DiscardedDiceTotal, Critical`) - the order in which
  RollDiceWithBoosts fills the result (total, natural, the other die, critical); `RollCritical`
  Success 1 / Fail 2 - the code's 1 for a high die, 2 for a natural 1.
- `ResistancesComponent.AC` at +0x10 int32 - the freeze changed the game's verdict.
- The tracer's `ECSEntityLog.Entity` is declared but never filled, and `Flags` is declared but not
  readable in the running build - **the two things found wrong**, both the fork's, not the game's.
Otherwise none found wrong.

**The end of the session.** `GK.Stage.Clean()` answered nothing to undo and `Verify` "no baseline:
GK.Stage.Begin was not called": the second `bridge-reload` had forgotten the second scope too;
`GK.Kit.Clear()` undid 0 rows. So the two goblins and the pins' boosts were left in the world - the
copy was then closed (`down`), and the next `up --level` loads the level afresh. **Suspected bug of
the shared stage** (for the coordinator): a `bridge-reload` in the middle of a scope leaves its
spawns and pins with no ledger that Clean or Kit.Clear reaches. No crash, no hang this session; the
other copies running disturbed nothing seen.

