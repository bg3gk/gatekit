# Wishes
1. Start events say "Игрок receives Одноручный арбалет, Тяжелый арбалет, Длинный лук..." (9 weapons) and "Астарион no longer has Короткий лук", but inventory shows none of these weapons. Who is "Игрок"? Where did the weapons go? Wanted: item events name the real receiving character, or say "(hidden/system container)" if not reachable by the party.
2. `party` shows "weapon Кинжал" but not whether there is a ranged weapon slot filled; would like melee + ranged sets listed.
   (dev answered 1-2: fixed; Игрок was a test stand-in)
3. roll: event says 'check succeeds (0)' - the die value and total are not shown (0?). Wanted: 'd20=13 +1 = 14 vs DC 10'.
   (dev answered 3: roll now prints a d20 line)
4. Loot resolve said `"received": "nothing"` while the event shows Астарион received 3 gold - gold not counted in "received".
5. `do use <illithid notes>` (item in inventory, StoryUse) -> "error: no answer from server in 30 s", no job created, no popup, nothing heard. Wanted: a way to READ a book/note item (`read <id>` printing its text), and a clear error why use failed.
6. A dialogue started while walking (Лаэзель). Only the last lines were shown; lines in between (from 'Чудовище. Тебе конец.' to her current line) were lost; also 'look 20' during the dialogue listed 0 characters although she talks to me. Wanted: `dialog` shows the conversation so far (all lines since it started), and look lists the speaker.
   (dev answered 4-6: fixed, new 'read <id>')
7. `do endturn all` -> 'no answer from server in 30 s'; afterwards only Астарион's turn had ended, Лаэзель's still open (also wait-turn just reported 'your turn' for her). endturn all should end every open turn or say which it could not.
8. `do move <marker xyz> partial` with an unreachable goal -> 'no path there' and nothing happened. I expected 'partial' to walk to the closest reachable point toward it. Also wanted: a hint HOW to get up (+16 m) - where are stairs/doors/ladders leading toward the marker.
9. look's object list seems to reach only ~20 m; the ladder (Артериальная сетка) 8 m away was invisible from 25 m. 'look 50' did not show more. A 'how do I get up/down here' (ladders, stairs) list in 'ways' would help.
10. The quest marker jumps each time I reach it (33 m W -> 80 m W -> 45 m N -> 8 m E -> 40 m SE -> 73 m W). Seems to be a chain of waypoints; would be nice if markers said 'waypoint 3 of N' or showed the final target distance too.
11. Opening a door started a story dialogue ('dialog': true) with an illithid; by the time I ran 'dialog' it said 'no dialogue' - the whole conversation (quest: connect the transmitter nerves) passed without me reading it or choosing answers, and a fight started. Wanted: `do use` that opens a dialogue should stop and print the dialogue's first line + answers (like the walking dialogue did); and a transcript of a finished dialogue ('dialog last').
   (noticed: 'ways' now lists connectors - helpful)
12. endturn all: 'failed: Астарион: no answer from server in 30 s' (2nd time), though the turn did pass.
   (dev answered 7-11: fixed; 10 explained)
13. endturn all after the update: 'ended: Астарион; failed: Лаэзель (no answer 30 s); still open: Астарион' - contradictory, and still 30 s timeouts.
14. At escape the log said 'quest closed: Спасти пленницу иллитидов - we left the prisoner in a pod'. I never saw this quest being given (no [quest] new quest event, journal never listed it). Wanted: every new quest announced in events, so I could have acted on it.
15. `do cast Target_Slash_New` -> {"ok":false,"error":"cooling down"}; `combat` had listed Slash as the best option for Laezel last round but stopped listing it - it would help if `combat`/`actions` said "Slash: used, recharges on short rest" instead of silently dropping it.
16. `do move <transmitter>` in combat refused ("too far: 10.9 m, 9 m left") instead of walking the 8.8 m it could ('nearest' was computed). For a plain move in a fight, I wanted it to walk as far as possible (like partial) or say "use partial".
17. Pommel strike left an imp "В нокауте (насовсем)" at 1/6 hp - unclear what that means (permanently unconscious? harmless?). A short gloss for unusual statuses would help.
18. COUNTDOWN in `combat` is great; would like it also in `status` and an explanation of what "turn" means for it (it ticked once per round, not per creature turn).
