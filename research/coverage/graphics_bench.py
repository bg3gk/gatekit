"""Moved: the graphics benchmark now lives in research/perf/graphics_bench.py (it measures more, and
uses the perf instance). This file only forwards, so that old notes still work.

    python research/perf/graphics_bench.py [LEVEL] [--runs N] [--kinds as_is,minimum]

Results of the first runs (2026-11-12) are in research/perf/load-speed.md, "Minimum graphics".
"""
import runpy
import sys
from pathlib import Path

if __name__ == "__main__":
    target = Path(__file__).resolve().parents[1] / "perf" / "graphics_bench.py"
    sys.argv[0] = str(target)
    runpy.run_path(str(target), run_name="__main__")
