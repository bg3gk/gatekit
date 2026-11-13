"""A tour: many short situations played one after another in the same running game while the
standing capture (seen.py) records every component type that comes to life.

    python research/coverage/tour.py nautiloid                 the whole tour of a level
    python research/coverage/tour.py nautiloid spells,throw    only these steps
    python research/coverage/tour.py --list
    python research/coverage/tour.py --auto dialogs,enemies,containers,doors,locked,traders 30
                                              steps taken from the level itself (at most 30 of a kind)

A tour is data (tours/<name>.json): a list of steps
    { "label": "throw", "server": "<Lua>", "ui": "<Lua>", "client": "<Lua>", "wait": 3,
      "then": [ {same keys, without label} ... ], "reset": true }
`server` / `ui` / `client` are run in that order; then the tour waits until the game has
finished what the act started (settle(): nothing on screen waits for the player, the host is
not casting) - at least a second, at most four times `wait`; "fixed": true makes it wait
exactly `wait` seconds instead; "until": a server Lua expression - the wait ends when it
turns true (a fight that plays itself reports its own end this way); `then` are further
acts of the same step. `reset` returns the world to the checkpoint afterwards (for steps that
change the story). In the Lua, {host} is the host's uuid. After every step whatever blocks the
game is removed (a dialogue is walked out of, the dead are raised, the fight is ended), the new
samples are moved into the library, and the step's yield is written to work/tour-<name>.json:
which types appeared for the first time in this step.

No window focus and no real input: everything goes through the bridge.
"""
import json
import os
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import probe  # noqa: E402
import seen  # noqa: E402
import situations  # noqa: E402

TOURS = HERE / "tours"


def unblock(game) -> list[str]:
    """Removes what holds the game up on both sides; returns what it did."""
    did = []
    try:
        steps = situations.leave_dialogue(game)
        if steps:
            did.append(f"left a dialogue in {steps} answers")
    except Exception as problem:  # noqa: BLE001 - a stuck dialogue must not stop the tour
        did.append(f"dialogue: {str(problem)[:80]}")
    try:
        # a spell left waiting for its target (a scroll that was read, a cast with nobody to hit)
        # keeps the character "casting" for ever: cancel it the way the player's right click does
        if game.server("return " + BUSY, timeout=10):
            game.ui('return pcall(GK.Command, "ActionCancelCommand")', timeout=10)
            did.append("cancelled a pending cast")
    except Exception:  # noqa: BLE001
        pass
    try:
        did += list(game.server("return GK.Stage.Unblock()") or [])
    except Exception as problem:  # noqa: BLE001
        did.append(f"server: {str(problem)[:80]}")
    return did


def game_over(game) -> str | None:
    """Why the game cannot go on, or None: it sits in the main menu, or the "game over" window
    is up (the server is then paused and refuses even to load a save)."""
    state = game.state("client", 1e9) or {}
    if state.get("gameState") == "Menu":
        return "the game is in the main menu"
    try:
        box = game.ui('local m = GK.ViewModel("ls.DCMessageBox") if not m then return nil end '
                      'local p = m:GetAllProperties() return p.IsGameOver and tostring(p.TextProperty) or nil', timeout=10)
    except Exception:  # noqa: BLE001 - no answer is judged by the caller's next call
        return None
    return f"game over window: {box}" if box else None


def restart(game):
    """The last resort: the instance is closed and started again in the same level, a new
    checkpoint is made. Costs minutes; used only when the game cannot go on (game_over)."""
    import os
    import subprocess
    stand = [sys.executable, str(HERE / "clean_stand.py")]
    environment = dict(os.environ, BG3_GATEKIT_START_TIMEOUT="300")
    environment.setdefault("BG3_GATEKIT_SE", str(HERE.parent / "builds" / "v34"))
    level = os.environ.get("COVERAGE_LEVEL", "GustavX:TUT_Avernus_C")
    subprocess.run(stand + ["down"], env=environment, check=False)
    subprocess.run(stand + ["up", "--level", level], env=environment, check=True)
    subprocess.run(stand + ["bridge-reload"], env=environment, check=False)
    game.skip_scenes()
    situations.stage(game)
    game.checkpoint()


BUSY = ('(function() local id = Osi.GetHostCharacter(); local e = Ext.Entity.Get(id); '
        'return e ~= nil and e.SpellCastIsCasting ~= nil end)()')


def settle(game, longest: float, shortest: float = 1.0, expect: float = 0) -> float:
    """Waits until the game has finished what the act started, instead of a fixed time: no
    dialogue, roll or message box on screen (the client's autoplay answers them as they
    appear) and the host is not casting - seen twice in a row, after at least `shortest`
    seconds and for at most `longest`. Returns the seconds waited."""
    started, calm = time.time(), 0
    if expect:
        # the act is known to open something (a dialogue takes three or four seconds to appear):
        # first wait for it to show, for at most `expect` seconds
        while time.time() - started < expect:
            try:
                if (game.ui("return GK.AutoplayInfo()", timeout=10) or {}).get("busy"):
                    break
            except Exception:  # noqa: BLE001
                pass
            time.sleep(0.3)
    time.sleep(shortest)
    while time.time() - started < longest:
        try:
            info = game.ui("return GK.AutoplayInfo()", timeout=10) or {}
            busy = info.get("busy") or game.server("return " + BUSY, timeout=10)
        except Exception:  # noqa: BLE001 - a missed look is just another look
            busy = True
        calm = 0 if busy else calm + 1
        if calm >= 2:
            break
        time.sleep(0.4)
    return time.time() - started


def act(game, step: dict, host: str) -> None:
    # COVERAGE_SPEED="enemyTurn=4,exploration=3": the game runs faster by situation (GK.Speed);
    # a world reset loads a save, which turns it off, so it is switched on before every step
    speed = os.environ.get("COVERAGE_SPEED")
    if speed:
        profile = ", ".join(f"{k} = {float(v)}" for k, v in (part.split("=") for part in speed.split(",") if "=" in part))
        try:
            game.server(f"GK.Speed.Enable({{ {profile} }})", timeout=10)
        except Exception:
            pass
    for side in ("server", "ui", "client"):
        code = step.get(side)
        if code:
            getattr(game, side)(code.replace("{host}", host), timeout=step.get("timeout", 30))
    wait = step.get("wait", 2)
    if step.get("until"):
        # an event of the game itself ends the wait: a server expression that turns true
        try:
            game.wait("server", step["until"], timeout=max(wait * 4, 20), every=0.3)
        except Exception:  # noqa: BLE001 - not reached in time: the step goes on, settle() below decides
            pass
        settle(game, longest=8, shortest=0.3)
    elif step.get("fixed"):
        time.sleep(wait)  # a step that needs its time whatever the game shows
    else:
        settle(game, longest=max(wait * 4, 20), shortest=min(wait, 1.0), expect=step.get("expect", 0))


def auto_steps(game, kinds: list[str], limit: int = 30, reset_every: int = 15) -> list[dict]:
    """Steps taken from what the level itself offers (GK.Stage.Scout and the story's dialogue
    list): "dialogs" - every registered dialogue whose speaker is on the level is started and
    walked out of; "enemies" - the host is put next to each hostile character and fights it for
    a few turns; "containers", "doors", "locked" - each is used (a locked one is unlocked
    first); "traders" - each trader is talked to; "items" - the nearest few items of every kind of
    use action the level has are used; "itemacts" - an item is carried, dropped, thrown, struck,
    worn, moved, locked and broken; "event", "scene", "exploration", "teleport",
    "portal", "region", "camp", "crime" - the host is put inside each trigger zone of that kind. The world is reset every few steps."""
    steps = []
    for kind in kinds:
        if kind == "dialogs":
            here = {row["uuid"] for row in game.server('return GK.Stage.Scout{ kind = "characters", limit = 2000 }', timeout=120)}
            seen_dialogs = set()
            for row in game.server("return GK.Dialogs()", timeout=60) or []:
                uuid = row["speaker"][-36:]
                if uuid in here and row["dialog"] not in seen_dialogs and len(seen_dialogs) < limit:
                    seen_dialogs.add(row["dialog"])
                    steps.append({"label": f"dialog:{row['dialog']}", "wait": 6, "expect": 6,
                                  "server": f'GK.Stage.OnStage("{uuid}"); return GK.Stage.Talk("{row["speaker"]}", "{row["dialog"]}")'})
            continue
        if kind == "dialogtree":
            # dialogues walked on purpose: the richest ones of the level (dice rolls, trade, nested
            # dialogues, many questions) are played several times - first answers, second answers,
            # and once more with the flags their branches are gated by already set
            import dialog_plan
            here = {row["uuid"] for row in game.server('return GK.Stage.Scout{ kind = "characters", limit = 3000 }', timeout=120)}
            known = {}
            for row in game.server("return GK.Dialogs()", timeout=60) or []:
                if row["speaker"][-36:] in here:
                    known.setdefault(row["dialog"], row["speaker"])
            described = []
            for dialog, speaker in known.items():
                info = dialog_plan.describe(dialog.rsplit("_", 1)[0])
                if info:
                    described.append((dialog_plan.richness(info), dialog, speaker, info))
            for _, dialog, speaker, info in sorted(described, key=lambda item: -item[0])[:limit]:
                for number, one in enumerate(dialog_plan.plan(info["name"].rsplit("/", 1)[-1], 3), 1):
                    flags = "".join(f'pcall(Osi.SetFlag, "{gate["flag"]}", {"NULL" if gate["type"] == "Global" else "H"}, 0) '
                                    for gate in one.get("flags", []))
                    talk = ('local H = Osi.GetHostCharacter() local NULL = "NULL_00000000-0000-0000-0000-000000000000" '
                            f'{flags}GK.Stage.OnStage("{speaker[-36:]}") return GK.Stage.Talk("{speaker}", "{dialog}")')
                    steps.append({"label": f"tree{number}:{dialog}", "wait": 0.2, "fixed": True, "reset": True,
                                  "ui": f'return GK.Autoplay(true, {{ pick = "{one["answers"]}" }})',  # before the talk
                                  "then": [{"server": talk, "wait": 12, "expect": 7},
                                           {"ui": 'return GK.Autoplay(true)', "wait": 0.2, "fixed": True}]})
            continue
        if kind == "items":
            # every kind of use action the level's items have (door, book, ladder, bed, chair,
            # lever, potion ...): the nearest few of each are used
            kinds_here = game.server("return GK.Stage.Usables()", timeout=120) or {}
            per_kind = max(2, limit // max(len(kinds_here), 1))
            for use_kind in sorted(kinds_here):
                rows = game.server(f'return GK.Stage.Usables("{use_kind}", {{ limit = {per_kind} }})', timeout=120) or []
                for row in rows:
                    steps.append({"label": f"use-{use_kind}:{row.get('template') or row['uuid']}", "wait": 4,
                                  "server": f'return GK.Stage.UseItem("{row["uuid"]}")'})
            continue
        if kind == "itemacts":
            # what else is done to items, on things made for the purpose: carried, dropped,
            # thrown, struck, worn, moved, locked, broken
            make = ('Scene = GK.Stage.Spawn{ template = "chest", distance = 3, items = { "potion", "dagger" } } '
                    'ScenePick = GK.Stage.Spawn{ template = "dagger", distance = 2 }[1] '
                    'SceneFoe = GK.Stage.Spawn{ template = "goblin", faction = "enemy", distance = 7 }[1] return ScenePick')
            for action, target in (("pickup", ""), ("drop", ""), ("move", ""), ("throw", ", SceneFoe"), ("pickup", ""),
                                   ("equip", ""), ("unequip", ""), ("drop", ""), ("attack", ""), ("lock", ""),
                                   ("unlock", ""), ("destroy", "")):
                item = "Scene[1]" if action in ("lock", "unlock", "attack", "destroy") else "ScenePick"
                steps.append({"label": f"item-{action}", "wait": 3, "keep": action != "destroy",
                              "server": f'if not ScenePick or not Ext.Entity.Get(ScenePick) then {make} end '
                                        f'return GK.Stage.ItemAction("{action}", {item}{target})'})
            continue
        if kind in ("event", "scene", "exploration", "teleport", "portal", "region", "camp", "crime"):
            # trigger zones: the host is put inside each, which sets off what the story hangs on it
            for row in game.server(f'return GK.Stage.Triggers("{kind}", {{ limit = {limit} }})', timeout=120) or []:
                steps.append({"label": f"{kind}:{row.get('name') or row['uuid']}", "wait": 5,
                              "server": f'return GK.Stage.Visit("{row["uuid"]}")'})
            continue
        rows = game.server(f'return GK.Stage.Scout{{ kind = "{kind}", limit = {limit} }}', timeout=120) or []
        for row in rows:
            uuid, label = row["uuid"], f"{kind}:{row.get('template') or row['uuid']}"
            if kind == "enemies":
                # the fight plays itself in the game: every turn that starts is ended at once
                steps.append({"label": label, "server": f'return GK.Stage.Visit("{uuid}")', "wait": 1, "then":
                              [{"server": f'return GK.Stage.Skirmish({{ "{uuid}" }}, 6)', "wait": 6,
                                "until": "not GK.Stage.SkirmishState().running"}]})
            elif kind == "traders":
                steps.append({"label": label, "server": f'return GK.Stage.Talk("{uuid}")', "wait": 6, "expect": 6})
            else:
                unlock = f'GK.Stage.Lock("{uuid}", false); ' if kind == "locked" else ""
                steps.append({"label": label, "wait": 4, "server":
                              f'GK.Stage.Visit("{uuid}"); {unlock}Osi.Use("{{host}}", "{uuid}", ""); return true'})
    for number, step in enumerate(steps, 1):
        if number % reset_every == 0:
            step["reset"] = True
        if step["label"].startswith("tree"):
            step["reset"] = True  # every pass of a walked dialogue starts from the same world
    return steps


def run(game, name: str, only: list[str] | None = None, steps: list[dict] | None = None) -> list[dict]:
    if steps is None:
        steps = json.loads((TOURS / f"{name}.json").read_text(encoding="utf-8"))
    log_path = HERE / "work" / f"tour-{name}.json"
    log = json.loads(log_path.read_text(encoding="utf-8")) if log_path.exists() else []
    situations.stage(game)
    game.ui("return GK.Autoplay(true)")  # rolls, boxes and dialogue answers are dealt with in the game
    can_reset = True
    try:
        unblock(game)  # a dialogue left over from an earlier run would keep the game from saving
        game.checkpoint()  # the point every reset of this tour returns to
    except Exception as problem:  # noqa: BLE001
        can_reset = False
        print(f"no checkpoint ({str(problem)[:120]}): this tour runs without world resets", flush=True)
    host = game.server("return Osi.GetHostCharacter()")
    known = set(seen.load_index())
    seen.start(game, "idle")
    known |= seen.new_names(game)  # what is alive before the first step is not any step's yield
    played = 0
    for step in steps:
        if only and step["label"] not in only:
            continue
        if os.environ.get("COVERAGE_RESUME") and any(old["label"] == step["label"] for old in log):
            continue  # played in an earlier run of this tour
        import gatekit
        if not gatekit.game_pids():
            # the game is gone (it closed between steps): start it again and carry on
            print(f"the game is not running before {step['label']}; restarting it", flush=True)
            restart(game)
            host = game.server("return Osi.GetHostCharacter()")
            game.ui("return GK.Autoplay(true)")
            seen.start(game, "idle")
            closures = closures + 1 if "closures" in dir() else 1
        started = time.time()
        entry = {"label": step["label"], "problems": []}
        seen.mark(game, step["label"])
        try:
            act(game, step, host)
            for further in step.get("then", []):
                act(game, further, host)
        except Exception as problem:  # noqa: BLE001 - one bad step must not stop the tour
            entry["problems"].append(str(problem)[:300])
        acted = time.time()
        dead_end = game_over(game)
        if dead_end:
            # nothing brings a game back from here but a restart: note the step and go on
            entry["problems"].append(f"{dead_end} - the game was restarted")
            entry["seconds"] = round(time.time() - started, 1)
            print(f"{step['label']:24} {entry['seconds']:6.1f} s  {dead_end}; restarting the game", flush=True)
            log = [old for old in log if old["label"] != step["label"]] + [entry]
            log_path.write_text(json.dumps(log, indent=1), encoding="utf-8")
            restart(game)
            host = game.server("return Osi.GetHostCharacter()")
            seen.start(game, "idle")
            continue
        entry["unblocked"] = unblock(game)
        if not step.get("keep"):
            try:
                game.server("GK.Stage.Reset() GK.Stage.Quiet() GK.Stage.Protect(true) "
                            "if GK.Stage.Guard then GK.Stage.Guard(true) end return true")
            except Exception as problem:  # noqa: BLE001
                entry["problems"].append(str(problem)[:300])
        # what came to life is asked by name every step (cheap); the samples are moved into the
        # library every few steps and before anything that loses the Lua state
        played += 1
        try:
            fresh = seen.new_names(game) - known
            if played % 8 == 0 or step.get("reset"):
                entry["samples"] = seen.drain(game)
        except Exception as problem:  # noqa: BLE001
            fresh = set()
            entry["problems"].append("capture: " + str(problem)[:300])
        entry["new"] = sorted(fresh)
        known |= fresh
        if os.environ.get("COVERAGE_VERIFY") and played % int(os.environ["COVERAGE_VERIFY"]) == 0:
            # the mapped stage of check.py on whatever is alive right now: a tour brings to life
            # what a quiet level never has, so this is where most layouts can be verified
            entry["verified"] = verify_now(game)
        entry["seconds"] = round(time.time() - started, 1)
        entry["acting"] = round(acted - started, 1)  # the rest is the tour's own overhead
        print(f"{step['label'][:44]:44} {entry['seconds']:5.1f} s (act {entry['acting']:4.1f})  new {len(entry['new']):3}  "
              f"{'; '.join(entry['problems'] + entry['unblocked'])[:160]}", flush=True)
        log = [old for old in log if old["label"] != step["label"]] + [entry]
        log_path.write_text(json.dumps(log, indent=1), encoding="utf-8")
        if step.get("reset") and can_reset:
            # no waiting for scenes to end: a big level always has some playing; they run 20x anyway
            try:
                game.reset(timeout=400, skip_intro=False)
            except PermissionError:
                # the game was reading the command file that very moment; the load itself goes on -
                # situations.stage() below waits until the world is back
                time.sleep(2)
            except Exception as problem:  # noqa: BLE001
                if "closed" not in str(problem):
                    raise
                # the game itself went down during the load: start it again and carry on
                print(f"the game closed during a reset after {step['label']}; restarting it", flush=True)
                restart(game)
            situations.stage(game)
            game.ui("return GK.Autoplay(true)")
            seen.start(game, "idle")
    try:
        seen.drain(game)
    except Exception as problem:  # noqa: BLE001
        print("the last drain failed:", str(problem)[:200])
    print(where_time_went(log), flush=True)
    return log


def verify_now(game) -> str:
    """Runs check.py's live comparison (no situations of its own) and says how it went."""
    import contextlib
    import io
    import check
    saved = os.environ.get("COVERAGE_SITUATIONS")
    os.environ["COVERAGE_SITUATIONS"] = ""
    out = io.StringIO()
    try:
        with contextlib.redirect_stdout(out):
            check.run(game)
        verdict = "ok"
    except AssertionError as problem:
        verdict = "CONTRADICTED: " + str(problem)[:300]
    except Exception as problem:  # noqa: BLE001
        verdict = "failed: " + str(problem)[:200]
    finally:
        if saved is None:
            os.environ.pop("COVERAGE_SITUATIONS", None)
        else:
            os.environ["COVERAGE_SITUATIONS"] = saved
    tail = [line for line in out.getvalue().splitlines() if "recorded as verified" in line]
    return verdict + (" - " + tail[-1].strip() if tail else "")


def where_time_went(log: list[dict]) -> str:
    """The tour's own account of its time, printed after every run: what to speed up next."""
    timed = sorted((entry for entry in log if entry.get("seconds")), key=lambda entry: -entry["seconds"])
    if not timed:
        return "no steps were played"
    seconds = [entry["seconds"] for entry in timed]
    slow = [entry for entry in timed if entry["seconds"] > 5]
    overhead = sum(entry["seconds"] - entry.get("acting", entry["seconds"]) for entry in timed)
    troubled = [entry for entry in timed if entry["problems"] or any("timed out" in text or "dialogue:" in text
                                                                     for text in entry.get("unblocked", []))]
    lines = [f"TIME: {len(timed)} steps, {sum(seconds):.0f} s in all, median {seconds[len(seconds) // 2]:.1f} s; "
             f"{len(slow)} steps over 5 s ({sum(e['seconds'] for e in slow):.0f} s), {len(troubled)} with problems; "
             f"the tour's own overhead {overhead:.0f} s"]
    for entry in timed[:5]:
        why = "; ".join(entry["problems"] + entry.get("unblocked", []))[:110]
        lines.append(f"  {entry['seconds']:6.1f} s  {entry['label'][:60]}  {why}")
    return "\n".join(lines)


if __name__ == "__main__":
    if len(sys.argv) < 2 or sys.argv[1] == "--list":
        for path in sorted(TOURS.glob("*.json")):
            labels = [step["label"] for step in json.loads(path.read_text(encoding="utf-8"))]
            print(f"{path.stem}: {', '.join(labels)}")
        sys.exit(0)
    sys.stdout.reconfigure(encoding="utf-8")
    tour, wanted = sys.argv[1], sys.argv[2].split(",") if len(sys.argv) > 2 else None
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 30
    game = probe.attach()  # clears sys.argv
    if tour == "--auto":
        situations.stage(game)  # waits out a level or save that is still loading
        level = game.server("return GK.Stage.CurrentLevel()")
        if not level:
            sys.exit("the game names no level: nothing to tour")
        run(game, f"auto-{level}", steps=auto_steps(game, wanted, limit))
    else:
        run(game, tour, wanted)
