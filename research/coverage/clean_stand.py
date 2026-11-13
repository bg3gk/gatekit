"""The stand, for coverage work: the same commands as stand/gatekit.py, but the instance's
`Mods` folder holds the GateKit bridge and nothing else.

    set BG3_GATEKIT_SE=E:\\bg3-gwynerva-extender\\se-fork\\bg3se\\x64\\Game Release
    python research/coverage/clean_stand.py up --level GustavX:TUT_Avernus_C
    python research/coverage/clean_stand.py exec server "return 1"
    python research/coverage/clean_stand.py run research/coverage/collect.py
    python research/coverage/clean_stand.py down

Why: the stand copies the player's mod paks into every instance at `up`. They are not in the
mod list, but the game mounts every pak in `Mods`, so a pak that overrides files would still
take part. Layouts measured for the coverage index must come from the unmodded game.

The instance is "coverage" unless BG3_GATEKIT_INSTANCE says otherwise; this wrapper refuses to
run on the installed game. It changes no file of the stand: it wraps one method at run time.
"""
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import oldrepo  # noqa: E402  (where the old Python stand is)

REPO = oldrepo.root()
sys.path.insert(0, str(REPO / "stand"))

os.environ.setdefault("BG3_GATEKIT_INSTANCE", "coverage")
if "--instance" in sys.argv:
    sys.exit("clean_stand.py: name the instance with BG3_GATEKIT_INSTANCE, not --instance")

import instances  # noqa: E402  (the stand's module; needs the path above)

_stock_sync_profile = instances.Instance.sync_profile


def _bridge_only_sync_profile(self, real_profile, bridge_pak):
    _stock_sync_profile(self, real_profile, bridge_pak)
    for pak in (self.profile / "Mods").glob("*.pak"):
        if pak.name != bridge_pak:
            pak.unlink()


instances.Instance.sync_profile = _bridge_only_sync_profile

import gatekit  # noqa: E402

if gatekit.INSTANCE is None:
    sys.exit("clean_stand.py: no instance; coverage work never runs on the installed game")

_stock_send = gatekit.Game._send


def _patient_send(self, *args, **kwargs):
    """The stand hands a command to the game by renaming a file into place; when the game is
    reading the previous one at that very moment Windows refuses the rename. Hours of tours make
    that rare race certain, so the rename is simply tried again."""
    import time
    for attempt in range(40):
        try:
            return _stock_send(self, *args, **kwargs)
        except PermissionError:
            if attempt == 39:
                raise
            time.sleep(0.05)


gatekit.Game._send = _patient_send


def running_games() -> list[tuple[int, str]]:
    """(pid, executable path) of every running copy of the game, whoever started it."""
    import subprocess
    listed = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "Get-CimInstance Win32_Process -Filter \"Name like 'bg3%'\" | ForEach-Object { \"$($_.ProcessId)|$($_.ExecutablePath)\" }"],
        capture_output=True, text=True, encoding="utf-8", errors="replace").stdout
    return [(int(pid), path) for pid, _, path in (line.partition("|") for line in listed.splitlines()) if pid.strip().isdigit()]


def close_leftovers() -> None:
    """Before a start: a copy of this instance that is still running (a start that timed out, a
    game left in the main menu) is closed, so two copies of one instance never run; copies of
    other instances are only named - they are somebody's, perhaps this session's own test run."""
    import subprocess
    marker = f"\\{os.environ['BG3_GATEKIT_INSTANCE']}\\game\\".lower()
    for pid, path in running_games():
        if marker in path.lower():
            print(f"clean_stand: closing a leftover copy of this instance (pid {pid})")
            subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
        else:
            print(f"clean_stand: another copy of the game is running (pid {pid}, {path})")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "up":
        close_leftovers()
    sys.exit(gatekit.main())
