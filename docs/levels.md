# Levels a test can start in

Every level below was started cold with `up --level GustavX:<Level>` (the game's own
`-testLoadLevel`), in a stand instance with only the bridge loaded; the catalogue with all
recorded facts is `research/coverage/levels.json`, written by `research/coverage/levels_survey.py`
(game 4.1.1.7631656, surveyed 2026-11-12).

| Level | What it is | Loads | Seconds | Characters | Items | Component types alive | Party |
| --- | --- | --- | ---: | ---: | ---: | ---: | --- |
| `Basic_Level_A` | the engine's basic level, nothing placed | yes | 91 | 253 | 716 | 584 | S_Player_Astarion |
| `TestLevel_Tom` | a developer's test level (22 items) | yes | 55 | 253 | 738 | 598 | S_Player_Astarion |
| `WLD_Main_A` | Act 1 (766 characters) | yes | 131 | 909 | 12851 | 688 | S_Player_Astarion |
| `WLD_Campfire_E` | an Act 1 camp | yes | 42 | 253 | 790 | 589 | S_Player_Astarion |
| `SYS_CC_I` | character creation | yes | 64 | 258 | 721 | 561 | S_GLO_CharacterCreationDummy_001, S_GLO_CharacterCreationDummy_002, S_GLO_CharacterCreationDummy_003, S_GLO_CharacterCreationDummy_004 |
| `END_NetherBrainArenas_E` | an arena of the finale | yes | 54 | 253 | 738 | 584 | S_Player_Astarion |
| `SCL_Main_A` | Act 2 (656 characters) | yes | 86 | 909 | 12057 | 677 | S_Player_Astarion |
| `CTY_Main_A` | Act 3, the Lower City (1457 characters) | yes | 161 | 1748 | 21323 | 756 | S_Player_Astarion |

How to read it:

- **Seconds** is from process start to a playable game *while two other games were running on
  the machine*; alone, the tutorial level takes about 43 s. Compare the rows with each other,
  not with a stopwatch.
- **Characters / items** are server entities. A level with "nothing placed" still has 253
  characters and about 720 items: the story's global objects (companions, quest items, camp
  followers) are created in every level.
- **Component types alive** counts the ECS component types that have at least one holder a
  few seconds after loading - a measure of how much of the game's machinery the level wakes.
- In every level the player is Astarion alone, at the level's start point, with the story not
  advanced; `SYS_CC_I` (character creation) starts with the four creation dummies as the party.
- Small levels to stage scenes in: `WLD_Campfire_E` (fastest), `TestLevel_Tom`,
  `END_NetherBrainArenas_E`, `Basic_Level_A`. The act levels bring their whole population.

Not surveyed yet: `CRE_Main_A`, `INT_Main_A`, `BGO_Main_A`, `IRN_Main_A`, `END_Main`,
`CMP_CentralCampfire_Day`; changing level inside a running game; whether the story's debug
procedures can advance the story state in a test-loaded level.
