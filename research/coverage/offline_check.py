"""The raw stage of check.py without a running game: every layout against the samples in the
library of seen components (seen.py) and the sizes in the coverage index.

    python research/coverage/offline_check.py [folder under layouts ...]

Writes work/check-results.json in the shape promote.py reads (entries of other folders are
kept). It can say "raw ok on N samples" or name the contradiction; it never verifies anything -
that needs the mapped stage in the game (check.py). Pointers inside samples are not followed
(the memory they pointed to is gone), entity handles are judged by shape only.
"""
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import check  # noqa: E402
import generate  # noqa: E402
import seen  # noqa: E402


def main() -> int:
    folders = sys.argv[1:]
    index = json.loads(check.INDEX.read_text(encoding="utf-8"))
    registry = {"client": {}, "server": {}}
    for row in index["components"]:
        if row.get("totalSize") is not None:
            registry[row["side"]][row["name"]] = {"TotalSize": row["totalSize"], "Mapped": False}
    layouts = {}
    for layout in generate.load(only_confirmed=False):
        if folders and layout["_path"].parent.name not in folders:
            continue
        try:
            generate.validate(layout)
        except Exception as problem:
            print(f"  not valid: {layout.get('component')}: {problem}")
            continue
        layouts[layout["component"]] = layout
    live = {"client": {}, "server": {}}
    added = check.add_library(live, layouts, registry)
    results = [check.check_layout(None, layout, registry, live) for layout in layouts.values()]
    kept = []
    if check.RESULTS.exists():
        kept = [r for r in json.loads(check.RESULTS.read_text(encoding="utf-8")) if r["component"] not in layouts]
    check.RESULTS.write_text(json.dumps(kept + results, indent=1), encoding="utf-8")
    ok = sum(1 for r in results if r["raw"] and r["instances"])
    bad = [r for r in results if r["raw"] is False]
    print(f"{len(results)} layouts, {added} library samples: raw ok with samples {ok}, "
          f"no samples {sum(1 for r in results if r['raw'] and not r['instances'])}, contradicted {len(bad)}")
    for result in bad:
        print(f"  FAIL {result['component']}")
        for problem in result["problems"][:3]:
            print(f"        {problem}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
