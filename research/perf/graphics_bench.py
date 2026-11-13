"""What the lowest graphics settings and a small window buy a test run.

    python research/perf/graphics_bench.py [LEVEL] [--runs N] [--kinds as_is,minimum]

For each kind of settings it starts the game in the perf instance N times (startup_profile's
measurement: stages, processor time, bytes read), and for each start also records: memory after
the level has settled, how much processor the game uses while nothing happens (10 s), the size
of the window's picture, and whether the game kept the settings file as it was written.
Only the instance's own graphicSettings.lsx is changed, and it is put back at the end.
Results: research/perf/runs/graphics-bench.json. Compare only runs taken under the same load.
"""
from __future__ import annotations

import argparse
import ctypes
import json
import re
import sys
import time
from ctypes import wintypes
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from startup_profile import ProcessProbe, RUNS, load_stand, one_run  # noqa: E402

# Names are the game's own (strings of the executable next to "AntiAliasing"); Type 0 = integer.
MINIMUM = {
    "ScreenWidth": 640, "ScreenHeight": 360, "Fullscreen": 0, "FakeFullscreenEnabled": 0,
    "ModelDetail": 0, "TextureDetail": 0, "TerrainTextureDetail": 0, "TextureFiltering": 0,
    "InstanceDistance": 0, "DetailDistance": 0, "AnimationLODDetail": 0, "DynamicCrowds": 0,
    "ShadowQuality": 0, "FogQuality": 0, "CloudsQuality": 0, "AntiAliasing": 0,
    "BloomEnabled": 0, "SSAOEnabled": 0, "SSREnabled": 0, "GodRaysEnabled": 0, "LensFlareEnabled": 0,
    "ClothGPUAcceleration": 0, "FrameCapEnabled": 1, "FrameCapFPS": 30, "VSync": 0,
}
KINDS = {"as_is": {}, "minimum": MINIMUM,
         "small_window": {"ScreenWidth": 640, "ScreenHeight": 360, "Fullscreen": 0, "FakeFullscreenEnabled": 0},
         "cap30": {"FrameCapEnabled": 1, "FrameCapFPS": 30}, "cap10": {"FrameCapEnabled": 1, "FrameCapFPS": 10}}

ENTRY = """                <node id="ConfigEntry">
                    <attribute id="MapKey" type="FixedString" value="{key}"/>
                    <attribute id="Type" type="int32" value="0"/>
                    <attribute id="Value" type="int32" value="{value}"/>
                </node>
"""
PATTERN = (r'(<attribute id="MapKey" type="FixedString" value="%s"/>\s*<attribute id="Type" type="int32" value=")\d+'
           r'("/>\s*<attribute id="Value" type=")\w+(" value=")[^"]*(")')


def with_settings(text: str, wanted: dict) -> str:
    """The settings file with every wanted key set (replaced where present, added otherwise)."""
    for key, value in wanted.items():
        pattern = re.compile(PATTERN % re.escape(key))
        if pattern.search(text):
            text = pattern.sub(lambda m: f"{m.group(1)}0{m.group(2)}int32{m.group(3)}{value}{m.group(4)}", text)
        else:
            text = text.replace("<children>\n", "<children>\n" + ENTRY.format(key=key, value=value), 1)
    return text


def read_settings(text: str) -> dict:
    return {key: value for key, value in re.findall(
        r'<attribute id="MapKey" type="FixedString" value="(\w+)"/>\s*<attribute id="Type"[^>]*/>\s*<attribute id="Value" type="\w+" value="([^"]*)"', text)}


def window_size(pid: int) -> list[int] | None:
    """Client size of the process's largest visible window (nothing is brought forward)."""
    user = ctypes.WinDLL("user32")
    best: list[int] | None = None

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def each(window, _):
        nonlocal best
        owner = wintypes.DWORD()
        user.GetWindowThreadProcessId(window, ctypes.byref(owner))
        if owner.value == pid and user.IsWindowVisible(window):
            rect = wintypes.RECT()
            user.GetClientRect(window, ctypes.byref(rect))
            if best is None or rect.right * rect.bottom > best[0] * best[1]:
                best = [rect.right, rect.bottom]
        return True
    user.EnumWindows(each, 0)
    return best


def idle_cores(pid: int, seconds: float = 10) -> float:
    probe = ProcessProbe(pid)
    a = probe.sample()
    time.sleep(seconds)
    b = probe.sample()
    probe.close()
    return round(((b["kernel"] + b["user"]) - (a["kernel"] + a["user"])) / (b["t"] - a["t"]), 2)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("level", nargs="?", default="GustavX:TUT_Avernus_C")
    parser.add_argument("--runs", type=int, default=3)
    parser.add_argument("--kinds", default="as_is,minimum")
    arguments = parser.parse_args()
    stand = load_stand(False)
    path = stand.PROFILE / "graphicSettings.lsx"
    if not path.exists():
        sys.exit(f"{path} is not there yet: start the instance once (startup_profile.py) so that its profile exists")
    original = path.read_text(encoding="utf-8")
    out = RUNS / "graphics-bench.json"
    result = json.loads(out.read_text(encoding="utf-8")) if out.exists() else {}
    run_arguments = argparse.Namespace(menu=False, sampler=False, inject=None, game_args=[], timeout=600, level=arguments.level,
                                       player_mods=False, mods=None, save=None, settle=15, sampler_interval=0.05, label="")
    try:
        for index in range(arguments.runs):
            for kind in arguments.kinds.split(","):       # alternating, so that a change of load hits every kind alike
                written = with_settings(original, KINDS[kind])
                path.write_text(written, encoding="utf-8")
                run_arguments.label = "gfx-" + kind
                run = one_run(stand, run_arguments, index + 1)
                analysis = run["analysis"]
                row = {"date": run["date"], "otherGames": run["otherGames"], "othersCores": analysis["total"].get("othersCores"),
                       "playable": analysis.get("clientRunning"), "cpu": analysis["total"].get("cpu"),
                       "readMB": analysis["total"].get("readMB"), "problem": run["problem"]}
                for stage in analysis["stages"].get("client", []):
                    if stage["state"] in ("LoadModule", "LoadLevel", "PrepareRunning") and "seconds" in stage:
                        row[stage["state"]] = stage["seconds"]
                if run["pid"] and not run["problem"]:
                    row["workingSetGB"], row["privateGB"] = analysis.get("endWorkingSetGB"), analysis.get("endPrivateGB")
                    row["idleCores"] = idle_cores(run["pid"])
                    row["window"] = window_size(run["pid"])
                    after = read_settings(path.read_text(encoding="utf-8"))
                    row["settingsKept"] = all(str(after.get(k)) == str(v) for k, v in KINDS[kind].items())
                    row["settingsChanged"] = {k: after.get(k) for k, v in KINDS[kind].items() if str(after.get(k)) != str(v)}
                stand.down()
                time.sleep(4)
                result.setdefault(kind, []).append(row)
                out.write_text(json.dumps(result, indent=1), encoding="utf-8")
                print(f"{kind:13} run {index + 1}: playable {row.get('playable')} s (module {row.get('LoadModule')}, prepare "
                      f"{row.get('PrepareRunning')}), cpu {row.get('cpu')} s, read {row.get('readMB')} MB, memory "
                      f"{row.get('workingSetGB')} / {row.get('privateGB')} GB, idle {row.get('idleCores')} cores, window "
                      f"{row.get('window')}, settings kept: {row.get('settingsKept')} {row.get('settingsChanged') or ''}; "
                      f"other games {row['otherGames']}, others' load {row['othersCores']} cores", flush=True)
    finally:
        path.write_text(original, encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
