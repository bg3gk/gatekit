"""Which system method touches which component, read off the function names of the macOS build.

    python research/coverage/macos_index.py build                    write the index
    python research/coverage/macos_index.py component <engine name>  methods that read / write it
    python research/coverage/macos_index.py system <engine name>     its methods and their components
    python research/coverage/macos_index.py names <text>             every named function containing a text

The macOS executable of the game keeps its symbol table (see macos_symbols.py). A system's
methods take an `ecs::WorldView<A, B const, ...>`: the exact list of component types the
method may touch, where `const` marks the ones it only reads. So the names alone give, for
the very build measured on Windows:

  - for every component: the system methods that write it and those that read it;
  - for every system: its methods by name, each with its components.

This needs no address matching between the builds: it tells a person (or a worker) *which
named function to look for*; finding that function in the Windows executable is the symbol
base's job.

Index: se-fork/reference/macos-symbols/worldviews.json (not committed: derived from the game).
"""
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
SYMBOLS = HERE.parent / "reference" / "macos-symbols" / "symbols-demangled.tsv"
INDEX = HERE.parent / "reference" / "macos-symbols" / "worldviews.json"
VIEW = "ecs::WorldView<"


def balanced(text: str, start: int) -> int:
    """Index just past the '>' that closes the '<' at text[start - 1]."""
    depth = 1
    at = start
    while depth and at < len(text):
        char = text[at]
        if char == "<":
            depth += 1
        elif char == ">" and text[at - 1] != "-":  # not the arrow of operator->
            depth -= 1
        at += 1
    return at


def split_top(text: str) -> list[str]:
    parts, depth, current = [], 0, ""
    for char in text:
        if char in "<(":
            depth += 1
        elif char in ">)":
            depth -= 1
        if char == "," and depth == 0:
            parts.append(current.strip())
            current = ""
        else:
            current += char
    if current.strip():
        parts.append(current.strip())
    return parts


def owners(name: str):
    """Yields (qualified function, [(component, read_only)]) for every function in a symbol's
    name that takes a WorldView (the symbol itself, or a function it is a local type of)."""
    at = 0
    while True:
        at = name.find(VIEW, at)
        if at < 0:
            return
        end = balanced(name, at + len(VIEW))
        inside = name[at + len(VIEW):end - 1]
        before = name[:at]
        match = re.search(r"([\w:~]+(?:<[^()]*>)?(?:::[\w~]+)*)\((?:[^()]*,\s*)?$", before)
        at = end
        if not match:
            continue
        function = match.group(1)
        components = []
        for argument in split_top(inside):
            read_only = argument.endswith(" const")
            components.append((argument[:-6] if read_only else argument, read_only))
        yield function, components


def build() -> dict:
    methods: dict[str, dict] = {}
    for line in open(SYMBOLS, encoding="utf-8"):
        address, kind, name = line.rstrip("\n").split("\t", 2)
        if VIEW not in name:
            continue
        for function, components in owners(name):
            entry = methods.setdefault(function, {"views": [], "address": None})
            if components not in entry["views"]:
                entry["views"].append(components)
            # the symbol that *is* the function (not a lambda or a local type inside it)
            if kind in "tT" and name.startswith(function + "(") or name.split(" ", 1)[-1].startswith(function + "("):
                entry["address"] = address
    by_component = defaultdict(list)
    by_class = defaultdict(list)
    for function, entry in methods.items():
        owner, _, method = function.rpartition("::")
        by_class[owner].append(method)
        for view in entry["views"]:
            for component, read_only in view:
                row = [function, "read" if read_only else "write"]
                if row not in by_component[component]:
                    by_component[component].append(row)
    index = {"methods": methods, "components": by_component, "classes": by_class}
    INDEX.write_text(json.dumps(index), encoding="utf-8")
    return index


def load() -> dict:
    if not INDEX.exists():
        return build()
    return json.loads(INDEX.read_text(encoding="utf-8"))


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    command = sys.argv[1]
    if command == "build":
        index = build()
        print(f"{len(index['methods'])} methods with a WorldView, in {len(index['classes'])} classes; "
              f"{len(index['components'])} component types named in them")
        return 0
    if command == "names":
        wanted = sys.argv[2]
        for line in open(SYMBOLS, encoding="utf-8"):
            if wanted in line:
                address, kind, name = line.rstrip("\n").split("\t", 2)
                if kind in "tT":
                    print(f"{address} {name[:300]}")
        return 0
    index = load()
    name = sys.argv[2]
    if command == "component":
        rows = index["components"].get(name, [])
        for access in ("write", "read"):
            found = sorted(function for function, how in rows if how == access)
            print(f"{access}: {len(found)}")
            for function in found:
                print(f"    {function}")
        return 0
    if command == "system":
        for method in sorted(index["classes"].get(name, [])):
            entry = index["methods"][f"{name}::{method}"]
            print(f"{name}::{method}   macOS {entry['address'] or '-'}")
            for view in entry["views"]:
                written = [c for c, read_only in view if not read_only]
                read = [c for c, read_only in view if read_only]
                print(f"    writes: {', '.join(written) or '-'}")
                print(f"    reads:  {', '.join(read) or '-'}")
        return 0
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main())
