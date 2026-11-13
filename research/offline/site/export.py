"""Exports the offline databases as static JSON, the data layer of a future search site.

    python export.py --out <output folder>                 # everything
    python export.py --out <output folder> --structure-only # names and structure, no game text
    python export.py --out <output folder> --limit 200      # a small sample, to try a front end on

Layout of the output (every file is plain JSON, small enough to fetch from a static host):

    manifest.json                     what was exported, from which builds, how many of each kind
    search/<kind>.json                [[name, id], ...] sorted by name — one list per kind
    search/prefix/<ab>.json           the same entries of every kind, bucketed by the first two
                                      characters of each word of the name, for type-ahead search
    scene/<aa>/<name>.json            one timeline (gatekit scene)
    class/<aa>/<name>.json            one engine class (gatekit about)
    enum/<name>.json                  one enumeration
    stat/<aa>/<name>.json             one stats entry with inherited fields
    osiris.json                       every Osiris function with its parameters

The output is derived from the game's files. Whether any of it may be published is for you to
decide; --structure-only leaves out localized text, dialog lines and stats values, and keeps
names, identifiers, types, counts and addresses.
"""
import argparse
import collections
import json
import pathlib
import re
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "kit"))
import gatekit  # noqa: E402

SAFE = re.compile(r"[^A-Za-z0-9_.\-]+")


def slug(name):
    return SAFE.sub("_", name)[:150]


def bucket(name):
    s = slug(name).lower()
    return (s[:2] if len(s) > 1 else s + "_") or "__"


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":"), default=str), encoding="utf-8")


def strip_text(value):
    """Removes game text from a result for --structure-only."""
    if isinstance(value, dict):
        return {k: strip_text(v) for k, v in value.items() if k not in ("text", "hit", "pseudo_code", "story_goals", "strings")}
    if isinstance(value, list):
        return [strip_text(v) for v in value]
    return value


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--out", required=True, help="folder to write (outside the repository)")
    p.add_argument("--structure-only", action="store_true", help="leave out localized text, dialog lines and stats values")
    p.add_argument("--limit", type=int, default=0, help="export at most this many of each kind")
    p.add_argument("--kinds", nargs="*", default=["scene", "class", "enum", "stat", "osiris"], help="what to export")
    args = p.parse_args()
    out = pathlib.Path(args.out)
    if pathlib.Path(__file__).resolve().parent.parent in out.resolve().parents:
        sys.exit("write the export outside the repository: it is derived from the game's files")
    started = time.time()
    clean = strip_text if args.structure_only else (lambda v: v)
    cap = (lambda items: items[:args.limit]) if args.limit else (lambda items: items)
    counts, search = {}, collections.defaultdict(list)

    if "scene" in args.kinds:
        names = cap([r["name"] for r in gatekit.rows("index", "SELECT name FROM timelines WHERE shadowed = 0 ORDER BY name")])
        for n, name in enumerate(names):
            write(out / "scene" / bucket(name) / f"{slug(name)}.json", clean(gatekit.scene(name)))
            search["scene"].append([name, name])
            if n % 1000 == 0:
                print(f"  scenes {n}/{len(names)}", file=sys.stderr)
        counts["scene"] = len(names)
    if "class" in args.kinds:
        names = cap([r["name"] for r in gatekit.rows(
            "symbols", "SELECT DISTINCT namespace || '::' || class AS name FROM symbols WHERE class <> '' AND namespace IN"
                       " ('ecl','esv','eoc','ls','ecs') OR namespace LIKE 'ecl::%' OR namespace LIKE 'esv::%' OR namespace LIKE 'eoc::%'"
                       " GROUP BY 1 ORDER BY 1") if r["name"] and "<" not in r["name"]])
        for n, name in enumerate(names):
            write(out / "class" / bucket(name.rsplit("::", 1)[-1]) / f"{slug(name)}.json", clean(gatekit.about_class(name)))
            search["class"].append([name, name])
            if n % 1000 == 0:
                print(f"  classes {n}/{len(names)}", file=sys.stderr)
        counts["class"] = len(names)
    if "enum" in args.kinds:
        enums = cap(gatekit.rows("symbols", "SELECT id, name, namespace, underlying, bitmask FROM enums ORDER BY name"))
        for e in enums:
            e["values"] = gatekit.rows("symbols", "SELECT name, value FROM enum_values WHERE enum_id = ? ORDER BY value", (e["id"],))
            write(out / "enum" / f"{slug(e['name'])}.json", e)
            search["enum"].append([e["name"], e["name"]])
        counts["enum"] = len(enums)
    if "stat" in args.kinds:
        stats = cap(gatekit.rows("index", "SELECT max(id) AS id, name, type, parent FROM stats GROUP BY name ORDER BY name"))
        for s in stats:
            if not args.structure_only:
                s["fields"] = gatekit.rows("index", "SELECT field, value FROM stat_fields WHERE stat_id = ?", (s["id"],))
            write(out / "stat" / bucket(s["name"]) / f"{slug(s['name'])}.json", s)
            search["stat"].append([s["name"], s["name"]])
        counts["stat"] = len(stats)
    if "osiris" in args.kinds:
        functions = gatekit.rows("index", "SELECT name, kind, params FROM osiris_functions ORDER BY name")
        write(out / "osiris.json", functions)
        search["osiris"] = [[f["name"], f["name"]] for f in functions]
        counts["osiris"] = len(functions)

    prefixes = collections.defaultdict(list)
    for kind, entries in search.items():
        entries.sort(key=lambda e: e[0].lower())
        write(out / "search" / f"{kind}.json", entries)
        for name, ident in entries:
            for word in set(w[:2].lower() for w in re.split(r"[^A-Za-z0-9]+", name) if len(w) >= 2):
                prefixes[word].append([kind, name, ident])
    for prefix, entries in prefixes.items():
        write(out / "search" / "prefix" / f"{slug(prefix)}.json", entries)
    meta = {r["key"]: r["value"] for r in gatekit.rows("symbols", "SELECT key, value FROM meta")}
    write(out / "manifest.json", {"exported": time.strftime("%Y-%m-%d %H:%M:%S"), "structure_only": args.structure_only,
                                  "counts": counts, "game_file_version": meta.get("game_file_version"),
                                  "toolkit_file_version": meta.get("toolkit_file_version"), "seconds": round(time.time() - started)})
    print(f"{out}: {counts}, {time.time() - started:.0f} s")


if __name__ == "__main__":
    main()
