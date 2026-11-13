"""Checks lsfdump against LSLib: every node and attribute of a .lsn file must equal the .lsx
that Divine wrote for the same .lsf.

    python compare.py <folder of .lsn> <folder of .lsx> [limit]

Numbers are compared as numbers (the two tools print floats differently), matrices element by
element to two decimals (LSLib prints them rounded), everything else as text.
"""
import collections
import pathlib
import sys
import xml.parsers.expat

FLOATY = {"float", "double", "fvec2", "fvec3", "fvec4", "mat2x2", "mat3x3", "mat3x4", "mat4x3", "mat4x4"}


def lsx_events(path):
    events = []

    def start(name, a):
        if name == "node":
            events.append(("N", a.get("id", "")))
        elif name == "attribute":
            events.append(("A", a["id"], a.get("type", ""), a.get("value", a.get("handle", ""))))

    def end(name):
        if name == "node":
            events.append(("E",))

    p = xml.parsers.expat.ParserCreate()
    p.StartElementHandler, p.EndElementHandler = start, end
    with open(path, "rb") as f:
        p.ParseFile(f)
    return events


def unescape(s):
    if "\\" not in s:
        return s
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == "\\" and i + 1 < len(s):
            out.append({"t": "\t", "n": "\n", "r": "\r", "\\": "\\"}.get(s[i + 1], s[i + 1]))
            i += 2
        else:
            out.append(c)
            i += 1
    return "".join(out)


def lsn_events(path):
    events = []
    with open(path, encoding="utf-8", newline="\n") as f:
        for line in f:
            line = line.rstrip("\n")
            kind = line[:1]
            if kind == "N":
                events.append(("N", unescape(line[2:])))
            elif kind == "A":
                parts = line.split("\t")
                events.append(("A", unescape(parts[1]), parts[2], unescape(parts[3]) if len(parts) > 3 else ""))
            elif kind == "E":
                events.append(("E",))
    return events


def same(kind, a, b):
    if a == b:
        return True
    if kind in FLOATY:
        if a in ("NaN", "inf", "-inf"):
            return True                      # LSLib prints these in the language of the system
        try:
            x, y = [float(v) for v in a.split()], [float(v) for v in b.split()]
        except ValueError:
            return False
        side = {4: 2, 9: 3, 16: 4}.get(len(x)) if kind.startswith("mat") else None
        if side:                             # LSLib prints matrices row by row, the file stores columns
            y = [y[c * side + r] for r in range(side) for c in range(side)]
        tolerance = 0.006 if kind.startswith("mat") else 1e-4
        return len(x) == len(y) and all(abs(p - q) <= tolerance * max(1.0, abs(p), abs(q)) for p, q in zip(x, y))
    strip = lambda s: "".join(ch for ch in s if ch >= " ").strip()
    return strip(a) == strip(b)               # LSLib drops control characters that XML cannot hold


def main():
    lsn_root, lsx_root = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
    limit = int(sys.argv[3]) if len(sys.argv) > 3 else 0
    files = sorted(lsn_root.rglob("*.lsn"))
    if limit:
        files = files[::max(1, len(files) // limit)]
    stats = collections.Counter()
    kinds = collections.Counter()
    examples = collections.defaultdict(list)
    for path in files:
        lsx = (lsx_root / path.relative_to(lsn_root)).with_suffix(".lsx")
        if not lsx.exists():
            stats["no .lsx to compare with"] += 1
            continue
        try:
            a, b = lsn_events(path), lsx_events(lsx)
        except Exception as e:                       # a broken file on either side
            stats["unreadable"] += 1
            examples["unreadable"].append(f"{path.name}: {e}")
            continue
        stats["files"] += 1
        if len(a) != len(b):
            stats["different structure"] += 1
            examples["structure"].append(f"{path.relative_to(lsn_root)}: {len(a)} events here, {len(b)} in the .lsx")
            continue
        bad = False
        for x, y in zip(a, b):
            if x[0] != y[0] or (x[0] != "E" and x[1] != y[1]):
                stats["different structure"] += 1
                examples["structure"].append(f"{path.relative_to(lsn_root)}: {x[:2]} here, {y[:2]} in the .lsx")
                bad = True
                break
            if x[0] == "A":
                stats["attributes"] += 1
                kinds[y[2]] += 1
                if x[2] != y[2]:
                    stats["different type name"] += 1
                    examples["type " + y[2]].append(f"{x[2]} here for {x[1]}")
                value = x[3].split("\t")[0] if y[2] in ("TranslatedString", "TranslatedFSString") else x[3]
                if not same(y[2], value, y[3]):
                    stats["different value"] += 1
                    bad = True
                    if len(examples["value " + y[2]]) < 5:
                        examples["value " + y[2]].append(f"{x[1]}: {value!r} here, {y[3]!r} in the .lsx   [{path.name}]")
        if bad:
            stats["files that differ"] += 1
    for k, v in stats.items():
        print(f"{k}: {v}")
    print("attribute types seen:", dict(kinds.most_common()))
    for k, items in examples.items():
        print(f"-- {k}")
        for item in items[:5]:
            print("   ", item)


if __name__ == "__main__":
    main()
