"""How much of what we have written down is still a machine's name, not ours.

    python research/coverage/naming.py            the tally
    python research/coverage/naming.py worst      the components most in need of a reader
    python research/coverage/naming.py --ns esv   only namespaces starting with this

Where a register has an upper bound - the game's own count of component types, systems, templates -
progress is "N of M" and needs no cleverness. Where it has none, this is the measure instead: among
the fields we have actually written down, how many still carry a name the machine invented
(`field_8`, `_Pad5`, `unknown_`) rather than a name that means something. No denominator of
"everything that exists" is needed, which is the point.

It also counts where each name came from, which is the ladder that matters:

    game      the game's own member name, read from the macOS build's FieldMeta - a fact
    toolkit   a Toolkit accessor's name - a fact
    inferred  worked out by reading the code - reasoning, and the bulk of our work
    unknown   we do not know, and the record says so rather than guessing

Padding is counted apart: `_Pad5` is not a name we failed to find, it is the absence of a field.

Pure data work over the committed layout files; no game and no database needed.
"""
import collections
import glob
import io
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
MACHINE = re.compile(r"^(field_|_Pad|unknown|unk_|pad\d|dummy)", re.IGNORECASE)
IN_BUILD = ("confirmed", "declared")


def layouts(namespace: str = ""):
    for path in sorted(glob.glob(str(HERE / "layouts" / "*" / "*.json"))):
        try:
            layout = json.load(io.open(path, encoding="utf-8"))
        except (OSError, ValueError):
            continue
        if layout.get("kind") == "system" or layout.get("status") not in IN_BUILD:
            continue
        if not (layout.get("fields") or []):
            continue
        if namespace and not (layout.get("component") or "").startswith(namespace):
            continue
        yield layout


def namespace_of(component: str) -> str:
    return "::".join(component.split("::")[:2])


def measure(namespace: str = ""):
    counts = collections.Counter()
    by_source = collections.Counter()
    by_namespace = collections.Counter()
    namespace_total = collections.Counter()
    components = []
    for layout in layouts(namespace):
        component = layout.get("component") or "?"
        space = namespace_of(component)
        here_total = here_machine = 0
        for field in layout["fields"]:
            name = field.get("name") or ""
            counts["total"] += 1
            here_total += 1
            by_source[field.get("source") or "(none)"] += 1
            namespace_total[space] += 1
            if name.startswith("_Pad"):
                counts["padding"] += 1
            if MACHINE.match(name):
                counts["machine"] += 1
                here_machine += 1
                by_namespace[space] += 1
            else:
                counts["named"] += 1
        components.append((here_machine, here_total, component))
    return counts, by_source, by_namespace, namespace_total, components


def main() -> int:
    args = sys.argv[1:]
    namespace = ""
    if "--ns" in args:
        at = args.index("--ns")
        namespace = args[at + 1]
        del args[at:at + 2]
    counts, by_source, by_namespace, namespace_total, components = measure(namespace)
    total = counts["total"]
    if total == 0:
        print("no layouts matched")
        return 1

    if args and args[0] == "worst":
        print("components most in need of a reader (6 or more fields):")
        ranked = sorted(((m / t, m, t, c) for m, t, c in components if t >= 6), reverse=True)
        for share, machine, fields, component in ranked[:30]:
            print(f"  {share:4.0%}  {machine:>3}/{fields:<3} {component}")
        return 0

    machine, padding = counts["machine"], counts["padding"]
    print(f"fields written down: {total}")
    print(f"  ours to read      : {counts['named']} ({counts['named'] / total:.1%})")
    print(f"  the machine's     : {machine} ({machine / total:.1%}), of which {padding} is padding")
    print(f"  owed a name       : {machine - padding} ({(machine - padding) / total:.1%})")
    print()
    print("where each name came from:")
    for source, n in by_source.most_common():
        print(f"  {source:<10} {n:>6} ({n / total:.1%})")
    facts = by_source["game"] + by_source["toolkit"]
    print(f"  -> established as fact (the game's or the Toolkit's own name): {facts} ({facts / total:.1%})")
    print()
    print("namespaces carrying the most machine names:")
    for space, n in by_namespace.most_common(15):
        print(f"  {space:<32} {n:>4} of {namespace_total[space]:<5} ({n / namespace_total[space]:.0%})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
