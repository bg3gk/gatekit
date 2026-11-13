"""Prints the game's own log lines of a recorded run (research/perf/runs/*.json), with seconds after
process start; state changes are left out unless --all.

    python research/perf/run_events.py <run.json | label> [--all]
"""
import json
import sys
from pathlib import Path

RUNS = Path(__file__).resolve().parent / "runs"


def main() -> None:
    what = sys.argv[1]
    path = Path(what) if Path(what).exists() else sorted(RUNS.glob(f"2*-{what}.json"))[-1]
    run = json.loads(path.read_text(encoding="utf-8"))
    print(path.name, run["what"], "other games:", run["otherGames"])
    for event in run["events"]:
        text = event["text"]
        if "--all" not in sys.argv and ("STATE SWAP" in text or "GameStateQueued" in text):
            continue
        print(f"{event['at']:8.2f}  {event['where'][:44]:44} {text[:120]}")


if __name__ == "__main__":
    main()
