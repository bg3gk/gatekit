"""How long a reload takes without leaving the process, and whether it gives a clean world.

    python research/perf/reload_bench.py save-load [--times 3]     AutoSave once, then change the world and
                                                          Osi.LoadGame the save; checks the change is gone
    python research/perf/reload_bench.py level WLD_Main_A [BACK]   Osi.TeleportPartiesToLevelWithMovie there (and back)
    python research/perf/reload_bench.py load <save folder name>   Osi.LoadGame of a given save

Works on the game already running in the perf instance (start one with
`startup_profile.py --keep`). Times come from the game's own log (state changes of the client),
counted from the moment the Lua call was sent. Results are appended to research/perf/runs/reload.jsonl.
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from startup_profile import ProcessProbe, RUNS, load_stand, read_log, state_swaps, usage_between  # noqa: E402


class Bench:
    def __init__(self) -> None:
        self.stand = load_stand(False)
        self.game = self.stand.Game()
        pids = self.stand.game_pids()
        if not pids:
            sys.exit("no game is running in the perf instance")
        self.probe = ProcessProbe(pids[0])
        self.others = len(self.stand.running_games()) - 1

    def log_events(self, since: float) -> list[dict]:
        logs = sorted(self.stand.EXE.parent.glob("network.*.log"), key=lambda p: p.stat().st_mtime)
        return [e for e in read_log(logs[-1]) if e["t"] >= since] if logs else []

    def measure(self, label: str, side: str, lua: str, timeout: float = 600) -> dict:
        """Sends the Lua, then waits until the client has left Running and come back to it."""
        before = self.probe.sample()
        started = time.time()
        try:
            self.game.lua(side, lua, timeout=15)
        except Exception as problem:  # noqa: BLE001 - the call may not answer once loading began
            print(f"  (no answer to the call: {str(problem)[:120]})")
        left = False
        states: list[tuple[float, str]] = []
        samples = [before]
        while time.time() - started < timeout:
            time.sleep(0.25)
            sample = self.probe.sample()
            if sample:
                samples.append(sample)
            swaps = state_swaps(self.log_events(started - 0.5))
            states = swaps["client"]
            if any(state != "Running" for _, state in states):
                left = True
            if left and states and states[-1][1] == "Running":
                break
            if not left and time.time() - started > 60:
                break
        swaps = state_swaps(self.log_events(started - 0.5))
        row = {"label": label, "date": time.strftime("%Y-%m-%d %H:%M:%S"), "otherGames": self.others, "left": left,
               "seconds": round(states[-1][0] - started, 2) if left and states and states[-1][1] == "Running" else None,
               "stages": {}}
        for which, changes in swaps.items():
            rows = []
            for index, (moment, state) in enumerate(changes):
                until = changes[index + 1][0] if index + 1 < len(changes) else None
                if until is not None and until - moment >= 0.25:
                    rows.append({"state": state, "at": round(moment - started, 2), "seconds": round(until - moment, 2),
                                 **usage_between(samples, moment, until)})
            row["stages"][which] = rows
        if row["seconds"]:
            row["usage"] = usage_between(samples, started, started + row["seconds"])
        RUNS.mkdir(exist_ok=True)
        with open(RUNS / "reload.jsonl", "a", encoding="utf-8") as out:
            out.write(json.dumps(row) + "\n")
        print(f"{label}: {row['seconds']} s  (other games: {self.others}; cpu {row.get('usage', {}).get('cpu')} s, "
              f"read {row.get('usage', {}).get('readMB')} MB)")
        for which in ("client", "server"):
            print("   " + which + ": " + ", ".join(f"{r['state']} {r['seconds']}" for r in row["stages"][which]))
        return row

    def wait_alive(self, timeout: float = 120) -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                if self.game.lua("server", "return Osi.GetHostCharacter() ~= nil", timeout=5):
                    return
            except Exception:  # noqa: BLE001
                time.sleep(1)
        raise SystemExit("the server's Lua did not answer after the reload")

    def world(self) -> dict:
        return self.game.lua("server", """
            local h = Osi.GetHostCharacter()
            local x, y, z = Osi.GetPosition(h)
            return { host = h, level = Osi.GetRegion(h), gold = Osi.GetGold(h), x = x, y = y, z = z,
                     flag = Osi.GetFlag("PERF_BENCH_MARK_00000000-0000-0000-0000-000000000000", h),
                     tag = Osi.GetVarInteger(h, "PERF_BENCH") }""")

    def saves(self) -> set[str]:
        root = self.stand.PROFILE / "PlayerProfiles"
        return {p.name for p in root.glob("*/Savegames/Story/*") if p.is_dir()}


def main() -> int:
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    bench = Bench()
    what = sys.argv[1]
    if what == "save-load":
        times = int(sys.argv[sys.argv.index("--times") + 1]) if "--times" in sys.argv else 3
        known = bench.saves()
        bench.game.lua("server", "Ext.Utils.GetGlobalSwitches().CanAutoSave = true")
        bench.game.lua("client", "Ext.Utils.GetGlobalSwitches().CanAutoSave = true")
        started = time.time()
        bench.game.lua("server", "Osi.AutoSave()")
        name = None
        while time.time() - started < 60 and not name:
            time.sleep(0.2)
            new = bench.saves() - known
            if new:
                name = sorted(new)[0]
        print(f"AutoSave: save {name!r} appeared after {time.time() - started:.1f} s")
        if not name:
            return 1
        time.sleep(3)
        clean = bench.world()
        print("  before:", clean)
        for index in range(times):
            bench.game.lua("server", """
                local h = Osi.GetHostCharacter()
                Osi.AddGold(h, 777)
                Osi.SetVarInteger(h, "PERF_BENCH", 42)
                local x, y, z = Osi.GetPosition(h)
                Osi.TeleportToPosition(h, x + 4, y, z, "", 0, 0, 0, 0, 1)""")
            time.sleep(1.5)
            print("  changed:", bench.world())
            row = bench.measure(f"LoadGame #{index + 1}", "server", f'Osi.LoadGame("{name}")')
            if not row["left"]:
                print("  the game never left Running: Osi.LoadGame did nothing with this name")
                return 1
            bench.wait_alive()
            after = bench.world()
            same = all(abs((after.get(k) or 0) - (clean.get(k) or 0)) < 0.5 for k in ("gold", "x", "z")) \
                and after.get("tag") == clean.get("tag")
            print("  after:", after, "-> clean world" if same else "-> NOT the saved world")
    elif what == "load":
        bench.measure("LoadGame " + sys.argv[2][:24], "server", f'Osi.LoadGame("{sys.argv[2]}")')
        bench.wait_alive()
        print("  after:", bench.world())
    elif what == "level":
        for target in sys.argv[2:]:
            bench.measure("level " + target, "server", f'Osi.TeleportPartiesToLevelWithMovie("{target}", "", "")')
            bench.wait_alive()
            print("  after:", bench.world())
    return 0


if __name__ == "__main__":
    sys.exit(main())
