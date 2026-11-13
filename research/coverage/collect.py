"""Collects what the game registers and what Script Extender covers of it, into
research/coverage/coverage.json, and writes the summary research/coverage/se-coverage.md.

Needs the GateKit build of Script Extender (Ext.Debug.GetComponentTypes):

    set BG3_GATEKIT_SE=E:\\bg3-gwynerva-extender\\se-fork\\bg3se\\x64\\Game Release
    python stand/gatekit.py --instance sefork run research/coverage/collect.py

The index is the list to work down: every entry is one thing the engine has, with whether
Script Extender exposes it. Verified-by-test status is kept in coverage.json across runs
(field "verified"), so a later collection does not lose it.
"""
import json
import time
from collections import Counter
from pathlib import Path

LEVEL = "GustavX:TUT_Avernus_C"
MODS: list[str] = []

HERE = Path(__file__).resolve().parent
INDEX = HERE / "coverage.json"
REPORT = HERE / "se-coverage.md"
NOTES = HERE / "report-notes.md"  # appended to the report as it is
CHUNK = 400

COMPONENTS = """
local all = Ext.Debug.GetComponentTypes()
table.sort(all, function(a, b) return a.Name < b.Name end)
local from, to = %d, %d
local out = {}
for i = from, math.min(to, #all) do out[#out + 1] = all[i] end
return { total = #all, items = out }
"""

SYSTEMS = """
local all = Ext.Debug.GetSystemTypes()
table.sort(all, function(a, b) return a.Name < b.Name end)
local from, to = %d, %d
local out = {}
for i = from, math.min(to, #all) do out[#out + 1] = all[i] end
return { total = #all, items = out }
"""


def fetch(run_lua, template):
    items, start = [], 1
    while True:
        part = run_lua(template % (start, start + CHUNK - 1))
        got = part.get("items") or []
        if isinstance(got, dict):  # an empty Lua table may arrive as {}
            got = list(got.values())
        items += got
        start += CHUNK
        if start > part["total"]:
            return items


def namespace(name: str) -> str:
    parts = name.split("::")
    return "::".join(parts[:-1]) if len(parts) > 1 else "(global)"


def run(game):
    if not game.client("return Ext.Debug.GetComponentTypes ~= nil"):
        raise AssertionError("this Script Extender has no Ext.Debug.GetComponentTypes; set BG3_GATEKIT_SE")

    previous = {}
    if INDEX.exists():
        old = json.loads(INDEX.read_text(encoding="utf-8"))
        for kind in ("components", "systems"):
            for item in old.get(kind, []):
                if item.get("verified"):
                    previous[(kind, item["side"], item["name"])] = item["verified"]

    index = {"collected": time.strftime("%Y-%m-%d %H:%M:%S"),
             "game": game.client("return Ext.Utils.GameVersion()"),
             "extender": game.client("return Ext.Utils.Version()"),
             "components": [], "systems": []}
    for side, run_lua in (("client", game.client), ("server", game.server)):
        for c in fetch(run_lua, COMPONENTS):
            index["components"].append({
                "side": side, "name": c["Name"], "index": c["Index"],
                "inlineSize": c.get("InlineSize"), "totalSize": c.get("TotalSize"),
                "oneFrame": bool(c.get("OneFrame")), "replicated": bool(c.get("Replicated")),
                "mapped": bool(c["Mapped"]),
                "verified": previous.get(("components", side, c["Name"])),
            })
        for s in fetch(run_lua, SYSTEMS):
            index["systems"].append({
                "side": side, "name": s["Name"], "index": s["Index"], "mapped": bool(s.get("Mapped")),
                "verified": previous.get(("systems", side, s["Name"])),
            })
    INDEX.write_text(json.dumps(index, ensure_ascii=False, indent=1), encoding="utf-8")
    REPORT.write_text(report(index), encoding="utf-8")
    print(summary(index))


def registered(index, kind, side):
    """What one side's world actually registers: components have a size there, systems are all listed."""
    items = [i for i in index[kind] if i["side"] == side]
    if kind == "components":
        items = [i for i in items if i.get("inlineSize") is not None]
    return items


def distinct(items):
    """One entry per name over both sides: covered if either side covers it, sized by the larger."""
    names = {}
    for item in items:
        entry = names.setdefault(item["name"], dict(item))
        entry["mapped"] = entry["mapped"] or item["mapped"]
        entry["totalSize"] = max(entry.get("totalSize") or 0, item.get("totalSize") or 0)
    return names


def tally(items):
    total, mapped = len(items), sum(1 for i in items if i["mapped"])
    return total, mapped, (100.0 * mapped / total if total else 0.0)


def summary(index) -> str:
    lines = []
    for kind in ("components", "systems"):
        for side in ("client", "server"):
            total, mapped, pct = tally(registered(index, kind, side))
            lines.append(f"{kind:10} {side:6} {mapped:5} of {total:5}  {pct:5.1f} %")
    return "\n".join(lines)


def declared_line() -> str:
    """How many of our layouts are in the build without a live check (status "declared")."""
    by_status = Counter()
    for path in (HERE / "layouts").rglob("*.json"):
        layout = json.loads(path.read_text(encoding="utf-8"))
        if layout.get("kind") != "system" and not layout.get("tag"):
            by_status[layout.get("status") or "hypothesis"] += 1
    return (f"Layouts written by this project (tags aside): {by_status['confirmed']} confirmed against live "
            f"instances, {by_status['declared']} declared from the game's code and NOT verified live (no "
            f"instance with data was available; marked so in the header), {by_status['hypothesis']} still "
            "hypotheses and not in the build.")


def report(index) -> str:
    out = ["# What Script Extender covers of the game", "",
           f"Collected {index['collected']} from the running game {index['game']} "
           f"(Script Extender v{index['extender']}, GateKit build) by `research/coverage/collect.py`. "
           "The full list is `research/coverage/coverage.json`; this page is its summary.", "",
           "\"Covered\" for a component means Script Extender has a layout for it, so "
           "`entity.<Name>` works in Lua. For a system it means `Ext.System.<Name>` gives an object. "
           "It does not mean every field is right: fields verified by a test are tracked "
           "separately in the index (`verified`).", "",
           "| What | Side | Covered | Total | % |", "| --- | --- | ---: | ---: | ---: |"]
    for kind in ("components", "systems"):
        for side in ("client", "server"):
            total, mapped, pct = tally(registered(index, kind, side))
            out.append(f"| {kind.capitalize()} | {side} | {mapped} | {total} | {pct:.1f} |")
    names = distinct(index["components"])
    total, mapped, pct = tally(list(names.values()))
    out += [f"| Components, distinct names | both | {mapped} | {total} | {pct:.1f} |"]
    system_names = distinct(index["systems"])
    total, mapped, pct = tally(list(system_names.values()))
    verified = len({i["name"] for i in index["components"] if i.get("verified")})
    out += [f"| Systems, distinct names | both | {mapped} | {total} | {pct:.1f} |", "",
            f"Verified by a scenario (fields read through Script Extender equal the game's memory on "
            f"live instances; `verified` in the index): {verified} component types. Every other "
            "covered type is declared by Script Extender but has not been checked by this project.", "",
            declared_line(), "",
            "A side's rows count what that side's world registers (the list of names is shared by "
            "both sides; a type the side does not register has no size there).", ""]

    missing = [i for i in names.values() if not i["mapped"]]
    tags = [i for i in missing if (i.get("totalSize") or 0) <= 1]
    sized = [i for i in missing if (i.get("totalSize") or 0) > 1]
    buckets = Counter()
    for item in sized:
        size = item["totalSize"]
        buckets["2-16" if size <= 16 else "17-64" if size <= 64 else "65-256" if size <= 256 else "over 256"] += 1
    out += ["## The gap in components", "",
            f"{len(missing)} component types have no layout. {len(tags)} of them are tags (one byte "
            "or less: they carry no data, only their presence on an entity matters) and need a "
            f"declaration, not reverse engineering. {len(sized)} carry data, "
            f"{sum(i['totalSize'] for i in sized)} bytes of structure in all: "
            + ", ".join(f"{buckets[b]} of {b} bytes" for b in ("2-16", "17-64", "65-256", "over 256")) + ".", "",
            "By namespace, most missing first:", "",
            "| Namespace | Missing | Of them tags | Covered | Total |", "| --- | ---: | ---: | ---: | ---: |"]
    by_ns_missing, by_ns_total, by_ns_tags = Counter(), Counter(), Counter()
    for item in names.values():
        ns = namespace(item["name"])
        by_ns_total[ns] += 1
        if not item["mapped"]:
            by_ns_missing[ns] += 1
            if (item.get("totalSize") or 0) <= 1:
                by_ns_tags[ns] += 1
    for ns, count in by_ns_missing.most_common(40):
        out.append(f"| `{ns}` | {count} | {by_ns_tags[ns]} | {by_ns_total[ns] - count} | {by_ns_total[ns]} |")
    rest = len(by_ns_missing) - 40
    if rest > 0:
        out.append(f"| ... {rest} more namespaces | | | | |")

    out += ["", "## Largest components without a layout", "",
            "| Component | Bytes |", "| --- | ---: |"]
    for item in sorted(sized, key=lambda i: -i["totalSize"])[:25]:
        out.append(f"| `{item['name']}` | {item['totalSize']} |")
    out.append("")
    if NOTES.exists():  # hand-written sections: what was measured about the work itself
        out.append(NOTES.read_text(encoding="utf-8"))
    return "\n".join(out)


if __name__ == "__main__":  # rebuild the report from the saved index, no game needed
    saved = json.loads(INDEX.read_text(encoding="utf-8"))
    REPORT.write_text(report(saved), encoding="utf-8")
    print(summary(saved))
