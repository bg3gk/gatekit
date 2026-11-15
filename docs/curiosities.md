# Curiosities: what the game turned out to be doing

Findings from GateKit's reverse-engineering work that may interest anyone who likes Baldur's
Gate 3, modder or not: leftovers of cut content, mechanics that were never finished, bugs, and
things the game quietly does behind the scenes.

Rules of this page:

- **Only confirmed findings.** Every entry says what was observed and how: in the unmodded game,
  started by GateKit in the test level `TUT_Avernus_C` - the Nautiloid - unless said otherwise.
- What is a fact and what is our reading of it are kept apart: "Seen" is what the game showed,
  "Reading" is our interpretation and may be wrong.
- Leads that are not confirmed yet do not go here; they stay in the technical notes.

---

## The whole game's story items are already "alive" on the Nautiloid

**Seen.** In a fresh game standing on the Nautiloid, the server already holds a live entity for
the *Torch of Revocation* (`S_UNI_LOW_TorchOfRevocation`, stats
`LOW_OskarsBeloved_TorchOfRevocation`) - an item of the Act 3 quest about Oskar and his beloved,
placed in the Lower City (`CTY_Main_A`), inside the chest `S_LOW_PhilgravesMansion_ClientChest`
in Mystic Carrion's mansion. It has 80 components, like any ordinary item in front of the player.

**Reading.** Items the story needs to refer to by name are stored as "globals" rather than as
part of a level, and globals are created when the game starts, wherever the player is. So from
the first minute the game keeps in memory items the player will not see for sixty hours.

How it was found: listing which server entities hold a particular marker
(`code:eoc::timeline::HideEquipmentComponent`, with `GK.Probe.Holders`); the item's data is in the game
index (`gatekit query about a042774f-ae71-4d19-9554-ba90cf34d046`).

## Seven weapons are hidden in every cutscene on purpose

**Seen.** With no dialogue or cutscene running, two items on the server already carry a marker
called `code:eoc::timeline::HideEquipmentComponent`: the Torch of Revocation above and the *Everburn
Blade* (Commander Zhalk's burning greatsword). Read in the running game, the torch also carries a
boost named `HiddenDuringCinematic`.

The game's data explains it: exactly seven weapon entries have `HiddenDuringCinematic` among
their default boosts - the plain torch (`WPN_Torch`, `WPN_Torch_StartLit`), the Torch of
Revocation, the Everburn Blade (`MAG_Fire_AlwaysDippedInFire_Greatsword`), a fire quarterstaff
(`MAG_Fire_FireDamage_Quarterstaff`), the conjured Flame Blade (`WPN_Scimitar_FlameBlade`) and
the Shadow Blade (`WPN_ShadowBlade`). In the code, `esv::TimelineSystem::OnBoostChanged` reacts
to that one boost type and `ProcessHideEquipmentUpdateRequests` puts the marker on the item or
takes it off.

**Reading.** Weapons that burn or are made of magic are taken out of every cutscene outright,
whatever the scene itself asks for - presumably because their fire and glow effects do not
survive the copy of the character that cutscenes use. If so, a character carrying a torch should
have empty hands in any dialogue, whatever that dialogue's own staging says; this was not watched
in play yet.

How it was found: the marker stood out when the game was asked which entities hold it; the code
that sets it was then traced, and the boost was read on the live item and looked up in the game
index
(`gatekit query sql index "SELECT s.name FROM stat_fields f JOIN stats s ON s.id = f.stat_id WHERE f.value LIKE '%HiddenDuringCinematic%'"`).

## Every level carries the whole cast

**Seen.** Started cold in eight different levels - from the engine's empty `Basic_Level_A` to the
Lower City - the server always holds at least 253 characters and about 720 items. An "empty"
level and a camp have exactly those; Act 1 adds 656 characters and twelve thousand items on top
(909 and 12,851), the Lower City 1,495 characters (1,748 and 21,323). The levels are listed in
[facts.md](facts.md).

**Reading.** The 253 are the story's global characters - companions, camp followers, everybody
the plot must be able to reach from anywhere - and they exist wherever the player is.

## The game knows eleven kinds of musical instrument - including bagpipes and a saxophone

**Seen.** Every instrument item carries one hidden byte (`code:eoc::stats::MusicalInstrumentComponent`):
read in the running game it is 5 on a lute (`ARM_Instrument_Lute_B`) and 6 on the Spider's Lyre.
The numbers are the game's own list `InstrumentType` in
`Shared/Stats/Generated/Structure/ValueLists.txt`: None 0, Bagpipes 1, Drum 2, Dulcimer 3, Flute 4,
Lute 5, Lyre 6, Horn 7, Shawm 8, Violin 9, Musicbox 10, Saxophone 11.

The five instruments a bard can actually play (drum, flute, lute, lyre, violin) use their own
names. The other six kinds are used by exactly one stat entry each, all named
`ARM_Countercharm_Instrument_*` (in `SharedDev`): the "Countercharm" flute is typed as bagpipes,
the drum as a dulcimer, the lute as a horn, the violin as a shawm, the lyre as a saxophone, and
there is a `ARM_Countercharm_Instrument_MusicBox`.

**Reading.** Countercharm is a bard feature of the tabletop rules that the released game does not
offer; these entries look like what is left of it, with the spare instrument kinds reused to tell
the Countercharm variants apart. Whether any of these items can be obtained in play was not
checked.

How it was found: the one byte stood out when all one-byte components were compared on the
server (everything else there is zero); the list and the stat entries are in the game index
(`gatekit query sql index "SELECT f.value, s.name FROM stat_fields f JOIN stats s ON s.id = f.stat_id WHERE f.field = 'InstrumentType'"`).

## Ninety thousand questions before every load: "is there a loose file?"

**Seen.** Before the game reads a file from its archives it first asks Windows whether a loose
file of the same name lies in the `Data` folder. Counted during one start straight into the
Nautiloid: 93,540 such questions, 89,515 of them answered "no such file". Loading an Act 1 save
with eleven mods asks 153,000. The questions are about timeline templates, character scripts,
resource lists, interface pictures, sound files - one by one.

**Reading.** This is the mechanism that lets a loose file override a packed one (how many mods and
the developers' own tools work), paid for on every load by everybody. One question costs a few
hundredths of a millisecond, so the total is a few seconds; an antivirus that inspects each query
would make it more (not measured).

How it was found: a sampling profiler showed a fifth of the loading work inside
`GetFileAttributesW`; a small DLL hooked into the game then counted the calls.

## A game that is doing nothing keeps two processor cores busy

**Seen.** Standing on the Nautiloid with nothing happening, the window in the background, the game
used between two and three processor cores; minimised, the same. A frame cap of 30 did not change
it.

**Reading.** The call stacks show the engine's worker threads circling between "let another
thread run" and very short waits instead of sleeping until there is work - quick to react, never
at rest.

How it was found: the processor time of the process over ten seconds, and a sampling profiler on
an idle game.

## Why weapons stay drawn for eight seconds after a fight

**Seen.** The server keeps a small component on a character for every *reason* its weapons should
be out or put away, each holding one number - a countdown in seconds. Read in the tick each
appeared, while a staged fight ran:

| Reason | Component | Seconds |
| --- | --- | ---: |
| joined a fight | `code:esv::unsheath::CombatJoiningComponent` | 8.0 |
| left a fight | `code:esv::unsheath::CombatLeavingComponent` | 8.0 |
| was resurrected | `code:esv::unsheath::ResurrectComponent` | 4.5 |
| drank a potion | `code:esv::unsheath::ConsumingComponent` | 0.6 |

**Read in the game's code (not measured in play).** The same family has four more members: being
summoned 3.0, applying a coating 2.35, combining items 1.5, picking a lock 1.5. One function
collects the reasons present on a character, sorts them by priority and lets
the strongest decide whether the weapons are sheathed, in melee stance or in ranged stance.

**Reading.** This is why a character keeps weapons drawn for some seconds after a fight ends, and
briefly puts them away to drink.

How it was found: the layouts of the "unsheath" components were worked out from the decompiled
systems; the four measured ones were then caught live, read every tick during a fight staged with
`GK.Stage`.
