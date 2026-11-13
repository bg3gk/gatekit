"""What to work on next: joins the coverage index, the layout files and the library of seen
components.

    python research/coverage/todo.py                 the tally
    python research/coverage/todo.py alive           not mapped, seen alive, no layout yet (work for a hypothesis)
    python research/coverage/todo.py waiting         layouts not confirmed yet, with whether samples exist
    python research/coverage/todo.py dead            not mapped and never seen alive
    add --json for a machine-readable list, --prefix esv::combat to narrow

A type counts once, whatever side it lives on (the index has a row per side).
"""
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent


def load() -> dict:
    """{name: {mapped, verified, size, oneFrame, replicated, sides, seen: {side: info}, layout: status or None}}"""
    index = json.loads((HERE / "coverage.json").read_text(encoding="utf-8"))
    types: dict[str, dict] = {}
    for row in index["components"]:
        entry = types.setdefault(row["name"], {"mapped": False, "verified": False, "size": row["totalSize"],
                                               "oneFrame": row["oneFrame"], "replicated": False, "sides": [],
                                               "seen": {}, "layout": None})
        entry["mapped"] |= bool(row["mapped"])
        entry["verified"] |= bool(row["verified"])
        entry["replicated"] |= bool(row["replicated"])
        entry["sides"].append(row["side"])
    seen_index = HERE / "seen" / "index.json"
    if seen_index.exists():
        for name, sides in json.loads(seen_index.read_text(encoding="utf-8")).items():
            if name in types:
                types[name]["seen"] = sides
    for path in (HERE / "layouts").glob("*/*.json"):
        layout = json.loads(path.read_text(encoding="utf-8"))
        name = layout.get("component")
        if name in types:
            types[name]["layout"] = layout.get("status") or "hypothesis"
            types[name]["layoutFile"] = f"{path.parent.name}/{path.name}"
            types[name]["live"] = layout.get("live")
    return types


def groups(types: dict) -> dict[str, list[str]]:
    out = defaultdict(list)
    for name, entry in sorted(types.items()):
        if entry["mapped"]:
            out["mapped"].append(name)
        elif entry["layout"]:
            out["waiting"].append(name)
        elif entry["seen"]:
            out["alive"].append(name)
        else:
            out["dead"].append(name)
    return out


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    as_json = "--json" in sys.argv
    prefix = sys.argv[sys.argv.index("--prefix") + 1] if "--prefix" in sys.argv else ""
    if prefix in args:
        args.remove(prefix)
    types = load()
    grouped = groups(types)
    if not args:
        total = len(types)
        print(f"{total} types: mapped {len(grouped['mapped'])}, layout waiting {len(grouped['waiting'])}, "
              f"alive without layout {len(grouped['alive'])}, never seen and no layout {len(grouped['dead'])}")
        print(f"verified live: {sum(1 for e in types.values() if e['verified'])}; "
              f"seen alive at all: {sum(1 for e in types.values() if e['seen'])}")
        spaces = Counter("::".join(name.split("::")[:2]) for name in grouped["alive"])
        print("alive without layout, by namespace:", ", ".join(f"{k} {v}" for k, v in spaces.most_common(25)))
        return 0
    names = [name for name in grouped[args[0]] if name.startswith(prefix)]
    if as_json:
        print(json.dumps({name: types[name] for name in names}, indent=1))
        return 0
    for name in names:
        entry = types[name]
        seen = "; ".join(f"{side} x{info['max']} [{', '.join(info['labels'][:3])}]" for side, info in entry["seen"].items())
        flags = "".join(flag for flag, on in (("R", entry["replicated"]), ("1", entry["oneFrame"])) if on)
        extra = f"  layout {entry.get('layoutFile')} ({entry['layout']})" if entry["layout"] else ""
        print(f"{entry['size'] or 0:5} {flags:2} {name}  {seen}{extra}")
    print(f"{len(names)} types")
    return 0


if __name__ == "__main__":
    sys.exit(main())
