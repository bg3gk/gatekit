"""What one "does this file exist?" question costs on this machine.

    python research/perf/exists_cost.py [folder ...]

The game asks Windows (GetFileAttributesW) about a loose file before it opens almost any packed
file. This asks the same question 20,000 times about files that are not there, in each folder
given (default: the installed game's Data, and the perf instance's Data, which reaches the same
folders through junctions), and prints microseconds per question.
"""
import ctypes
import os
import sys
import time
from pathlib import Path

kernel = ctypes.WinDLL("kernel32")
kernel.GetFileAttributesW.argtypes = [ctypes.c_wchar_p]
kernel.GetFileAttributesW.restype = ctypes.c_uint32


def cost(folder: str, count: int = 20000) -> float:
    paths = [rf"{folder}\Public\GustavDev\TimelineTemplates\{n:08x}-dfa2-4067-afea-3aef0440f0a7\{n:08x}.lsf" for n in range(count)]
    started = time.perf_counter()
    for path in paths:
        kernel.GetFileAttributesW(path)
    return (time.perf_counter() - started) / count * 1e6


def main() -> None:
    game = Path(os.environ.get("BG3_GAME", r"C:\Program Files (x86)\Steam\steamapps\common\Baldurs Gate 3"))
    folders = sys.argv[1:] or [str(game / "Data")] + [str(p / "perf" / "game" / "Data") for p in game.parent.glob("Baldurs Gate 3 *Kit")]
    for folder in folders:
        if os.path.isdir(folder):
            runs = [cost(folder) for _ in range(3)]
            print(f"{min(runs):7.1f} us per question (best of 3: {', '.join(f'{r:.1f}' for r in runs)})  {folder}")


if __name__ == "__main__":
    main()
