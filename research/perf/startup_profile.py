"""Where the seconds of a game start go.

    python research/perf/startup_profile.py --level GustavX:TUT_Avernus_C --runs 3 --label idle
    python research/perf/startup_profile.py --save <save folder> --player-mods
    python research/perf/startup_profile.py --menu                 stop at the main menu
    python research/perf/startup_profile.py --report               table of every run recorded so far

Starts the game through the stand in an instance of its own ("perf" unless BG3_GATEKIT_INSTANCE /
BG3_GATEKIT_INSTANCE says otherwise; never the installed game), and while it starts samples the
process four times a second: processor time, bytes read, memory. Afterwards it reads the game's
own log (bin\network.*.log and gold.*.log of the instance: every change of state of the client
and of the server with milliseconds) and prints one table: each state, how long it lasted, how
much processor time the whole process used meanwhile (1.0 = one core busy all the time), how
much was read from disk. Every run is kept as JSON in research/perf/runs/.

The number of other games running and how busy the rest of the machine was are recorded with
each run: only runs under the same load can be compared.

Options: --keep leaves the game running after the last run; --settle N samples N more seconds
after the game is playable; --sampler runs research/perf/stack_sampler.py against the game while it
starts (stacks of every thread, see that file); `-- <game arguments>` are passed on.
"""
from __future__ import annotations

import argparse
import ctypes
import datetime
import json
import os
import re
import subprocess
import sys
import threading
import time
from ctypes import wintypes
from pathlib import Path

HERE = Path(__file__).resolve().parent
RUNS = HERE / "runs"
sys.path.insert(0, str(HERE.parent))
import oldrepo  # noqa: E402  (where the old Python stand is)

REPO = oldrepo.root()
sys.path.insert(0, str(REPO / "stand"))

kernel = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
kernel.OpenProcess.restype = wintypes.HANDLE
kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel.CloseHandle.argtypes = [wintypes.HANDLE]


class IO_COUNTERS(ctypes.Structure):
    _fields_ = [(name, ctypes.c_ulonglong) for name in
                ("ReadOps", "WriteOps", "OtherOps", "ReadBytes", "WriteBytes", "OtherBytes")]


class MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t),
                ("PrivateUsage", ctypes.c_size_t)]


def _filetime(value: wintypes.FILETIME) -> int:
    return (value.dwHighDateTime << 32) | value.dwLowDateTime


EPOCH_OFFSET = 116444736000000000  # 100 ns ticks between 1601 and 1970


def system_times() -> tuple[float, float]:
    """(busy seconds, total seconds) summed over all processors since boot."""
    idle, kernel_time, user = wintypes.FILETIME(), wintypes.FILETIME(), wintypes.FILETIME()
    kernel.GetSystemTimes(ctypes.byref(idle), ctypes.byref(kernel_time), ctypes.byref(user))
    total = (_filetime(kernel_time) + _filetime(user)) / 1e7  # kernel time includes idle time
    return total - _filetime(idle) / 1e7, total


class ProcessProbe:
    """Counters of one process, read without touching it (PROCESS_QUERY_LIMITED_INFORMATION)."""

    def __init__(self, pid: int) -> None:
        self.pid = pid
        self.handle = kernel.OpenProcess(0x1000, False, pid)
        if not self.handle:
            raise OSError(f"cannot open process {pid}: error {ctypes.get_last_error()}")

    def close(self) -> None:
        kernel.CloseHandle(self.handle)

    def created(self) -> float:
        """Unix time at which the process was created."""
        times = [wintypes.FILETIME() for _ in range(4)]
        kernel.GetProcessTimes(self.handle, *[ctypes.byref(t) for t in times])
        return (_filetime(times[0]) - EPOCH_OFFSET) / 1e7

    def sample(self) -> dict | None:
        times = [wintypes.FILETIME() for _ in range(4)]
        if not kernel.GetProcessTimes(self.handle, *[ctypes.byref(t) for t in times]):
            return None
        code = wintypes.DWORD()
        kernel.GetExitCodeProcess(self.handle, ctypes.byref(code))
        if code.value != 259:  # STILL_ACTIVE
            return None
        io = IO_COUNTERS()
        kernel.GetProcessIoCounters(self.handle, ctypes.byref(io))
        memory = MEMORY_COUNTERS()
        memory.cb = ctypes.sizeof(memory)
        psapi.GetProcessMemoryInfo(self.handle, ctypes.byref(memory), memory.cb)
        busy, total = system_times()
        return {"t": time.time(), "kernel": _filetime(times[2]) / 1e7, "user": _filetime(times[3]) / 1e7,
                "readBytes": io.ReadBytes, "readOps": io.ReadOps, "writeBytes": io.WriteBytes,
                "otherOps": io.OtherOps, "workingSet": memory.WorkingSetSize, "private": memory.PrivateUsage,
                "faults": memory.PageFaultCount, "sysBusy": busy, "sysTotal": total}


class Watcher(threading.Thread):
    """Waits for the instance's game process and samples it until told to stop."""

    def __init__(self, gatekit, every: float = 0.25) -> None:
        super().__init__(daemon=True)
        self.gatekit, self.every = gatekit, every
        self.samples: list[dict] = []
        self.created: float | None = None
        self.pid: int | None = None
        self.stop = threading.Event()

    def run(self) -> None:
        probe = None
        while not self.stop.is_set():
            if probe is None:
                pids = self.gatekit.game_pids()
                if not pids:
                    time.sleep(0.02)
                    continue
                try:
                    probe = ProcessProbe(pids[0])
                except OSError:
                    continue
                self.pid, self.created = probe.pid, probe.created()
            sample = probe.sample()
            if sample is None:  # the process ended (the stand restarts it when the mods came up off)
                probe.close()
                probe = None
                self.samples.clear()
                continue
            self.samples.append(sample)
            time.sleep(self.every)
        if probe:
            probe.close()


LINE = re.compile(r"^\[(\d\d)-(\d\d)-(\d{4}) (\d\d):(\d\d):(\d\d):(\d+)\]\[(.*?) (\d+) ([^\]]+)\]: (.*)$")


def read_log(path: Path) -> list[dict]:
    """The game's log lines as {t (unix seconds, the log is in UTC), where, text}."""
    events = []
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        found = LINE.match(line)
        if not found:
            continue
        day, month, year, hour, minute, second, fraction, source, _, function, text = found.groups()
        moment = datetime.datetime(int(year), int(month), int(day), int(hour), int(minute), int(second),
                                   tzinfo=datetime.timezone.utc).timestamp() + int(fraction) / 1000
        events.append({"t": moment, "where": function, "file": source.rsplit("\\", 1)[-1], "text": text.strip()})
    return events


def state_swaps(events: list[dict]) -> dict[str, list[tuple[float, str]]]:
    swaps: dict[str, list[tuple[float, str]]] = {"client": [], "server": []}
    for event in events:
        found = re.match(r"(CLIENT|SERVER) STATE SWAP - from: (\w+), to: (\w+)", event["text"])
        if found:
            swaps[found.group(1).lower()].append((event["t"], found.group(3)))
    return swaps


def usage_between(samples: list[dict], start: float, end: float) -> dict:
    """What the process used between two moments, interpolated from the samples."""
    if not samples:
        return {}

    def at(moment: float) -> dict:
        previous = samples[0]
        for sample in samples:
            if sample["t"] >= moment:
                span = sample["t"] - previous["t"]
                share = (moment - previous["t"]) / span if span > 0 else 0
                share = min(max(share, 0), 1)
                return {key: previous[key] + (sample[key] - previous[key]) * share for key in sample}
            previous = sample
        return samples[-1]

    a, b = at(start), at(end)
    wall = max(end - start, 1e-9)
    cpu = (b["kernel"] + b["user"]) - (a["kernel"] + a["user"])
    system = (b["sysBusy"] - a["sysBusy"])
    return {"cpu": round(cpu, 2), "cores": round(cpu / wall, 2), "kernelShare": round((b["kernel"] - a["kernel"]) / cpu, 2) if cpu > 0.05 else None,
            "readMB": round((b["readBytes"] - a["readBytes"]) / 2 ** 20, 1), "readOps": int(b["readOps"] - a["readOps"]),
            "othersCores": round(max(system - cpu, 0) / wall, 2)}


def analyse(created: float, samples: list[dict], events: list[dict], playable: float | None) -> dict:
    """Stages of the start: per side, each state with its start (seconds after process creation),
    its length and what the whole process used during it."""
    result: dict = {"stages": {}, "protocol": [], "notes": []}
    swaps = state_swaps(events)
    end_of_run = playable or (samples[-1]["t"] if samples else created)
    first_log = events[0]["t"] if events else None
    if first_log:
        result["beforeFirstLogLine"] = {"seconds": round(first_log - created, 3), **usage_between(samples, created, first_log)}
    for side, changes in swaps.items():
        rows = []
        if changes:
            rows.append({"state": "(process start to first state)", "at": 0.0, "seconds": round(changes[0][0] - created, 3),
                         **usage_between(samples, created, changes[0][0])})
        for index, (moment, state) in enumerate(changes):
            until = changes[index + 1][0] if index + 1 < len(changes) else None
            row = {"state": state, "at": round(moment - created, 3)}
            if until is not None:
                row["seconds"] = round(until - moment, 3)
                row.update(usage_between(samples, moment, until))
            rows.append(row)
        result["stages"][side] = rows
    for event in events:
        if "LoadProtocol" in event["text"] or "Thread" in event["text"] or "Failed" in event["text"] \
                or "Discovery" in event["text"]:
            result["protocol"].append({"at": round(event["t"] - created, 3), "text": event["text"][:160]})
    running = [moment for moment, state in swaps["client"] if state == "Running"]
    if running:
        result["clientRunning"] = round(running[0] - created, 3)
    server_running = [moment for moment, state in swaps["server"] if state == "Running"]
    if server_running:
        result["serverRunning"] = round(server_running[0] - created, 3)
    result["total"] = usage_between(samples, created, end_of_run)
    result["total"]["seconds"] = round(end_of_run - created, 3)
    if samples:
        result["peakWorkingSetGB"] = round(max(s["workingSet"] for s in samples) / 2 ** 30, 2)
        result["peakPrivateGB"] = round(max(s["private"] for s in samples) / 2 ** 30, 2)
        result["endWorkingSetGB"] = round(samples[-1]["workingSet"] / 2 ** 30, 2)
        result["endPrivateGB"] = round(samples[-1]["private"] / 2 ** 30, 2)
    return result


def print_run(run: dict) -> None:
    analysis = run["analysis"]
    print(f"\n=== {run['label']} / {run['what']} / run {run['index']}  ({run['date']}; other games: {run['otherGames']}; "
          f"rest of the machine used {analysis['total'].get('othersCores', '?')} cores on average) ===")
    before = analysis.get("beforeFirstLogLine")
    if before:
        print(f"process start to the first log line: {before['seconds']:.2f} s, cpu {before.get('cpu', 0):.1f} s, "
              f"read {before.get('readMB', 0):.0f} MB")
    for side in ("client", "server"):
        print(f"{side}:")
        print(f"  {'at, s':>8} {'lasted, s':>10} {'cpu, s':>7} {'cores':>6} {'read MB':>8} {'reads':>7}  state")
        for row in analysis["stages"].get(side, []):
            if "seconds" in row and row["seconds"] < 0.25 and row["state"] not in ("Running",):
                continue  # the many states passed in a frame or two
            print(f"  {row['at']:8.2f} {row.get('seconds', float('nan')):10.2f} {row.get('cpu', float('nan')):7.1f} "
                  f"{row.get('cores', float('nan')):6.2f} {row.get('readMB', float('nan')):8.0f} {row.get('readOps', 0):7d}  {row['state']}")
    total = analysis["total"]
    print(f"playable (client Running) after {analysis.get('clientRunning', float('nan')):.2f} s; stand reported {run.get('standSeconds')} s; "
          f"cpu {total.get('cpu')} s ({total.get('cores')} cores on average), read {total.get('readMB')} MB in {total.get('readOps')} reads; "
          f"memory at the end {analysis.get('endWorkingSetGB')} GB in use / {analysis.get('endPrivateGB')} GB committed")


def summary(runs: list[dict]) -> None:
    """One line per run and, per (label, what, other games), the median of the main stages."""
    import statistics
    names = ["LoadModule", "LoadSession", "LoadLevel", "SwapLevel", "PrepareRunning"]
    print(f"\n{'label':16} {'what':28} {'games':>5} {'others':>6} {'1st log':>7} {'c.Init*':>7} "
          + " ".join(f"{'c.' + n[:9]:>11}" for n in names) + f" {'s.LoadMod':>9} {'playable':>8}  {'read MB':>7} {'cpu s':>6}")
    groups: dict[tuple, list[list[float]]] = {}
    for run in runs:
        analysis = run["analysis"]
        client = {}
        for row in analysis["stages"].get("client", []):
            client[row["state"]] = client.get(row["state"], 0) + row.get("seconds", 0)
        server = {}
        for row in analysis["stages"].get("server", []):
            server[row["state"]] = server.get(row["state"], 0) + row.get("seconds", 0)
        first_module = next((row["at"] for row in analysis["stages"].get("client", []) if row["state"] == "LoadModule"), float("nan"))
        values = [analysis.get("beforeFirstLogLine", {}).get("seconds", float("nan")), first_module,
                  *[client.get(n, float("nan")) for n in names], server.get("LoadModule", float("nan")),
                  analysis.get("clientRunning", float("nan")), analysis["total"].get("readMB", float("nan")),
                  analysis["total"].get("cpu", float("nan"))]
        print(f"{run['label'][:16]:16} {run['what'][:28]:28} {run['otherGames']:5d} {analysis['total'].get('othersCores', 0):6.1f} "
              f"{values[0]:7.1f} {values[1]:7.1f} " + " ".join(f"{v:11.1f}" for v in values[2:7])
              + f" {values[7]:9.1f} {values[8]:8.1f}  {values[9]:7.0f} {values[10]:6.0f}")
        groups.setdefault((run["label"], run["what"], run["otherGames"]), []).append(values)
    print("\nmedians (c.Init* = seconds from process start to the client entering LoadModule):")
    for (label, what, others), rows in groups.items():
        medians = [statistics.median(v[i] for v in rows) for i in range(len(rows[0]))]
        print(f"{label[:16]:16} {what[:28]:28} {others:5d} {'n=' + str(len(rows)):>6} {medians[0]:7.1f} {medians[1]:7.1f} "
              + " ".join(f"{v:11.1f}" for v in medians[2:7]) + f" {medians[7]:9.1f} {medians[8]:8.1f}  {medians[9]:7.0f} {medians[10]:6.0f}")


def load_stand(player_mods: bool):
    """The stand's module, bound to this tool's instance. Without player_mods the instance's
    Mods folder is left with the bridge only (the game mounts every pak it finds there)."""
    # The stand is being renamed; both spellings of its names are served until that is over.
    # (The old one is put together from parts so that the renaming script leaves it alone.)
    old = "DEV" + "KIT"
    instance = os.environ.get("BG3_GATEKIT_INSTANCE") or os.environ.get(f"BG3_{old}_INSTANCE") or "perf"
    os.environ["BG3_GATEKIT_INSTANCE"] = os.environ[f"BG3_{old}_INSTANCE"] = instance
    os.environ.setdefault("BG3_GATEKIT_START_TIMEOUT", "400")
    os.environ.setdefault(f"BG3_{old}_START_TIMEOUT", os.environ["BG3_GATEKIT_START_TIMEOUT"])
    import importlib
    import instances
    if not player_mods:
        stock = instances.Instance.sync_profile

        def bridge_only(self, real_profile, bridge_pak):
            stock(self, real_profile, bridge_pak)
            for pak in (self.profile / "Mods").glob("*.pak"):
                if pak.name != bridge_pak:
                    pak.unlink()
        instances.Instance.sync_profile = bridge_only
    saved = sys.argv
    sys.argv = [saved[0]]
    try:
        try:  # PERF_STAND names the stand's module outright (to attach to a game an older stand started)
            stand = importlib.import_module(os.environ.get("PERF_STAND") or "gatekit")
        except ImportError:
            stand = importlib.import_module(old.lower())
    finally:
        sys.argv = saved
    if stand.INSTANCE is None:
        sys.exit("startup_profile.py never runs on the installed game")
    return stand


def one_run(stand, arguments, index: int) -> dict:
    others = [str(path) for pid, path in stand.running_games() if pid not in stand.game_pids()]
    log_folder = stand.EXE.parent
    watcher = Watcher(stand)
    watcher.start()
    wanted = "Menu" if arguments.menu else "Running"
    if arguments.menu:  # the stand waits for Running; the main menu is a state of its own
        original = stand.Game.wait_state
        stand.Game.wait_state = lambda self, _wanted, side="client", timeout=300, since=None: \
            original(self, "Menu", side, timeout, since)
    sampler = None
    started = time.time()
    problem = None
    try:
        if arguments.sampler:
            sampler = subprocess.Popen([sys.executable, str(HERE / "stack_sampler.py"), "--wait-exe", str(stand.EXE),
                                        "--out", str(RUNS / f"stacks-{arguments.label}-{int(started)}.json"),
                                        "--interval", str(arguments.sampler_interval)],
                                       creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
        if arguments.inject:  # a DLL loaded into the game as soon as its process exists (perf/inject.py)
            injector = subprocess.Popen([sys.executable, str(HERE / "inject.py"), arguments.inject, "--wait-exe", str(stand.EXE)])
        extra = arguments.game_args or None
        if arguments.menu:
            extra = list(arguments.game_args)  # an empty list: no -continueGame
        stand.up(extra, timeout=arguments.timeout, level=arguments.level,
                 mods=None if arguments.player_mods and not arguments.level else (arguments.mods.split(",") if arguments.mods else []),
                 save=arguments.save)
    except Exception as failure:  # noqa: BLE001 - recorded with the run
        problem = str(failure)
    finally:
        if arguments.menu:
            stand.Game.wait_state = original
    playable = time.time()
    stand_seconds = round(playable - started, 1)
    time.sleep(arguments.settle)
    if sampler:
        try:
            sampler.send_signal(__import__("signal").CTRL_BREAK_EVENT)
            sampler.wait(60)
        except Exception:  # noqa: BLE001
            sampler.kill()
    watcher.stop.set()
    watcher.join(5)
    created = watcher.created or started
    logs = sorted((p for p in log_folder.glob("*.log") if p.stat().st_mtime >= started - 2), key=lambda p: p.name)
    events = sorted((event for path in logs for event in read_log(path)), key=lambda e: e["t"])
    analysis = analyse(created, watcher.samples, events, None)
    what = ("level " + arguments.level) if arguments.level else ("save " + arguments.save[:20]) if arguments.save \
        else "menu" if arguments.menu else "continue"
    if arguments.player_mods:
        what += " +mods"
    run = {"label": arguments.label, "what": what, "index": index, "date": time.strftime("%Y-%m-%d %H:%M:%S"),
           "wanted": wanted, "otherGames": len(others), "otherGamePaths": others, "pid": watcher.pid,
           "created": created, "launchDelay": round(created - started, 3), "standSeconds": stand_seconds,
           "problem": problem, "gameArgs": arguments.game_args, "logs": [p.name for p in logs],
           "analysis": analysis, "samples": watcher.samples,
           "events": [{"at": round(e["t"] - created, 3), "where": e["where"], "text": e["text"][:200]} for e in events]}
    RUNS.mkdir(exist_ok=True)
    out = RUNS / f"{time.strftime('%Y%m%d-%H%M%S')}-{arguments.label}.json"
    out.write_text(json.dumps(run, indent=1), encoding="utf-8")
    run["file"] = str(out)
    return run


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--level")
    parser.add_argument("--save")
    parser.add_argument("--menu", action="store_true")
    parser.add_argument("--mods", help="comma-separated mod folders besides the bridge")
    parser.add_argument("--player-mods", action="store_true", help="keep the player's mod paks and mod list")
    parser.add_argument("--runs", type=int, default=1)
    parser.add_argument("--label", default="run")
    parser.add_argument("--settle", type=float, default=3)
    parser.add_argument("--timeout", type=float, default=600)
    parser.add_argument("--keep", action="store_true")
    parser.add_argument("--inject", help="a DLL to load into the game at its start (test games only)")
    parser.add_argument("--sampler", action="store_true")
    parser.add_argument("--sampler-interval", type=float, default=0.05)
    parser.add_argument("--report", nargs="*", help="print the table of recorded runs (optionally: only these labels)")
    parser.add_argument("game_args", nargs="*")
    arguments = parser.parse_args()
    if arguments.report is not None:
        runs = [json.loads(p.read_text(encoding="utf-8")) for p in sorted(RUNS.glob("2*.json"))]
        runs = [r for r in runs if not r.get("problem") and (not arguments.report or r["label"] in arguments.report)]
        summary(runs)
        return 0
    if not (arguments.level or arguments.save or arguments.menu):
        arguments.level = "GustavX:TUT_Avernus_C"
    stand = load_stand(arguments.player_mods)
    if stand.game_pids():
        sys.exit("the instance's game is already running; stop it first (down)")
    done = []
    for index in range(1, arguments.runs + 1):
        run = one_run(stand, arguments, index)
        done.append(run)
        print_run(run)
        if run["problem"]:
            print("PROBLEM:", run["problem"])
        if not (arguments.keep and index == arguments.runs):
            stand.down()
            time.sleep(4)
    if len(done) > 1:
        summary([r for r in done if not r["problem"]])
    return 0


if __name__ == "__main__":
    sys.exit(main())
