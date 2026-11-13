"""What a game that is only standing there costs, and what lowers it.

    python research/perf/idle_cost.py [--seconds 8]

On the game already running in the perf instance: measures the processor it uses while nothing
happens, then again after each change, and puts everything back:
  - the engine switch YieldOnLostFocus on (client), then off again;
  - the frame cap set through the engine switches, if they are exposed;
  - the window minimised (no focus is taken), then restored.
Also prints which of the load-related engine switches Script Extender exposes, with values.
"""
from __future__ import annotations

import ctypes
import json
import sys
import time
from ctypes import wintypes
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from graphics_bench import idle_cores  # noqa: E402
from startup_profile import RUNS, load_stand  # noqa: E402

SWITCHES = ["YieldOnLostFocus", "NoStoryPatch", "AlwaysPatchStory", "SlowHDDMode", "FileLoadingLog", "StoryLog",
            "TimelinesOnOff", "ServerMonitor", "UseInGameConsole", "FrameCapEnabled", "FrameCapFPS", "CanAutoSave",
            "ServerFrameCap", "ClientFrameCap", "ForceStoryPatching", "CheckRequirements", "LoadTextShaders",
            "DisableStoryPatching", "EnableSteamP2P", "DirectConnect", "FileSavingLog", "ResetTutorialsOnNewGame",
            "DoUnlearnCheck", "ShroudEnabled", "FogOfWarEnabled", "EnablePortmapping", "UpdateOffstageOverlayMaterials"]


def windows_of(pid: int) -> list[int]:
    user = ctypes.WinDLL("user32")
    found = []

    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def each(window, _):
        owner = wintypes.DWORD()
        user.GetWindowThreadProcessId(window, ctypes.byref(owner))
        if owner.value == pid and user.IsWindowVisible(window) and user.GetWindowTextLengthW(window) > 0:
            found.append(window)
        return True
    user.EnumWindows(each, 0)
    return found


def main() -> int:
    seconds = float(sys.argv[sys.argv.index("--seconds") + 1]) if "--seconds" in sys.argv else 8
    stand = load_stand(False)
    pids = stand.game_pids()
    if not pids:
        sys.exit("no game is running in the perf instance")
    pid = pids[0]
    game = stand.Game()
    rows = {}
    exposed = game.lua("client", """
        local s = Ext.Utils.GetGlobalSwitches()
        local out = {}
        for _, name in ipairs({ %s }) do
            local ok, value = pcall(function() return s[name] end)
            if ok and value ~= nil then out[name] = tostring(value) end
        end
        return out""" % ", ".join(f'"{name}"' for name in SWITCHES))
    print("engine switches exposed (client):", json.dumps(exposed, indent=1))
    rows["switches"] = exposed

    def measure(label: str) -> None:
        time.sleep(1.5)
        rows[label] = idle_cores(pid, seconds)
        print(f"{label:34} {rows[label]:5.2f} cores", flush=True)

    measure("as started (window in background)")
    if "YieldOnLostFocus" in exposed:
        game.lua("client", "Ext.Utils.GetGlobalSwitches().YieldOnLostFocus = true")
        game.lua("server", "pcall(function() Ext.Utils.GetGlobalSwitches().YieldOnLostFocus = true end)")
        measure("YieldOnLostFocus = true")
        game.lua("client", f"Ext.Utils.GetGlobalSwitches().YieldOnLostFocus = {exposed['YieldOnLostFocus']}")
        game.lua("server", f"pcall(function() Ext.Utils.GetGlobalSwitches().YieldOnLostFocus = {exposed['YieldOnLostFocus']} end)")
    user = ctypes.WinDLL("user32")
    windows = windows_of(pid)
    for window in windows:
        user.ShowWindowAsync(window, 7)     # SW_SHOWMINNOACTIVE
    measure("window minimised")
    alive = game.lua("server", "return Osi.GetHostCharacter() ~= nil")
    print("  Lua still answers while minimised:", alive)
    for window in windows:
        user.ShowWindowAsync(window, 8)     # SW_SHOWNA: back, without activating
    measure("window restored")
    RUNS.mkdir(exist_ok=True)
    with open(RUNS / "idle-cost.jsonl", "a", encoding="utf-8") as out:
        out.write(json.dumps({"date": time.strftime("%Y-%m-%d %H:%M:%S"), **rows}) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
