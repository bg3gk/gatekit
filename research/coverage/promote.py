"""Sets the status of hypothesis layouts from the last check (work/check-results.json).

    python research/coverage/promote.py [folder under layouts ...]

A hypothesis becomes "confirmed" - allowed into the header - when the raw stage passed on at
least one live or captured instance that held data (not all zero bytes) and the author's
confidence is not "low". A layout of high confidence for which the game offered no instance
with data becomes "declared": it goes into the header too, but marked "live": "not verified"
with the reason, and is counted apart everywhere. Otherwise it stays a hypothesis and
"status_reason" says what is missing. Also copies the one-frame flag from the coverage index into the layout.
"""
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import generate  # noqa: E402

results = {r["component"]: r for r in json.loads((HERE / "work" / "check-results.json").read_text(encoding="utf-8"))}
index = json.loads((HERE / "coverage.json").read_text(encoding="utf-8"))
one_frame = {i["name"] for i in index["components"] if i.get("oneFrame")}
folders = sys.argv[1:]
changed = {"confirmed": [], "declared": [], "hypothesis": []}
for layout in generate.load(only_confirmed=False):
    path = layout.pop("_path")
    if folders and path.parent.name not in folders:
        continue
    if layout.get("tag") or layout.get("status") == "confirmed":
        continue
    layout.pop("live", None)
    layout.pop("live_reason", None)
    name = layout["component"]
    result = results.get(name)
    layout["oneFrame"] = name in one_frame
    reason = None
    try:
        generate.validate(layout)
    except Exception as problem:
        reason = f"the layout file is not valid yet: {problem}"
    if reason is None:
        if result is None:
            reason = "not checked yet"
        elif result["raw"] is not True:
            reason = "the raw check failed: " + "; ".join(result["problems"][:2])
        elif result["instances"] == 0:
            reason = "no live or captured instance: needs a situation that creates it"
        elif not result.get("nonzero"):
            reason = "only empty (all-zero) instances were seen: the member boundaries are untested"
        elif layout.get("confidence") == "low":
            reason = "the author's confidence is low: types fit the live data, meanings are not established"
    untested = reason and (reason.startswith("no live or captured instance") or reason.startswith("only empty"))
    if layout.get("needs_windows"):
        # offsets known from the macOS build only: never declared on that strength (see p80 in the journal)
        untested, reason = False, layout["needs_windows"]
    if untested and layout.get("confidence") == "high":
        # Established from code with high confidence, the size agrees with the registry, but the
        # game gave nothing to compare with: into the build, marked as not verified live.
        layout["status"] = "declared"
        layout["live"], layout["live_reason"] = "not verified", reason
        layout.pop("status_reason", None)
        changed["declared"].append(name)
    elif reason:
        layout["status"], layout["status_reason"] = "hypothesis", reason
        changed["hypothesis"].append(name)
    else:
        layout["status"] = "confirmed"
        layout.pop("status_reason", None)
        layout["raw_check"] = (f"{results[name]['instances']} instances, research/coverage/check.py raw stage, "
                               f"game {index['game']}")
        changed["confirmed"].append(name)
    path.write_text(json.dumps(layout, indent=2, ensure_ascii=False), encoding="utf-8", newline="\n")
for status, names in changed.items():
    print(f"{status}: {len(names)}")
    for name in names:
        print("   ", name)
