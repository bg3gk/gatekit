"""Compares the component sizes the macOS reimplementation of Script Extender measured
(tdimino/bg3se-macos, cloned read-only into se-fork/reference/bg3se-macos) with the sizes the
Windows game registers (research/coverage/coverage.json).

    python research/coverage/compare_macos.py

Their sizes come from decompiled AddComponent<T> instantiations of the ARM64 build
(ghidra/offsets/components/COMPONENT_SIZES_*.md); ours from the running game's registry.
Agreement says the two builds lay the type out alike; a difference is worth a look before a
layout from one side is trusted on the other.
"""
import json
import re
from pathlib import Path

HERE = Path(__file__).resolve().parent
TABLES = HERE.parent / "reference" / "bg3se-macos" / "ghidra" / "offsets" / "components"
INDEX = HERE / "coverage.json"

ROW = re.compile(r"^\|\s*([a-z]+::[\w:]+)\s*\|\s*(0x[0-9A-Fa-f]+)\s*\|\s*(\d+)\s*\|")


def macos_sizes() -> dict[str, int]:
    sizes = {}
    for table in sorted(TABLES.glob("COMPONENT_SIZES_*.md")):
        for line in table.read_text(encoding="utf-8", errors="replace").splitlines():
            row = ROW.match(line)
            if row and int(row.group(2), 16) == int(row.group(3)):
                sizes[row.group(1)] = int(row.group(3))
    return sizes


def windows_sizes() -> dict[str, dict]:
    types = {}
    for item in json.loads(INDEX.read_text(encoding="utf-8"))["components"]:
        if item.get("totalSize") is None:
            continue
        entry = types.setdefault(item["name"], {"size": 0, "mapped": False})
        entry["size"] = max(entry["size"], item["totalSize"])
        entry["mapped"] |= item["mapped"]
    return types


def main() -> None:
    theirs, ours = macos_sizes(), windows_sizes()
    shared = sorted(set(theirs) & set(ours))
    same = [name for name in shared if theirs[name] == ours[name]["size"]]
    different = [name for name in shared if theirs[name] != ours[name]["size"]]
    print(f"macOS tables: {len(theirs)} component sizes; Windows registry: {len(ours)} types")
    print(f"in both: {len(shared)}; same size: {len(same)}; different: {len(different)}")
    print(f"only in the macOS tables (not registered by the Windows build measured): {len(set(theirs) - set(ours))}")
    unmapped = [name for name in shared if not ours[name]["mapped"]]
    print(f"of the shared ones, without a Windows layout here: {len(unmapped)}")
    print("\ndifferent sizes (component, macOS, Windows, mapped here):")
    for name in different:
        print(f"  {name}  {theirs[name]}  {ours[name]['size']}  {'yes' if ours[name]['mapped'] else 'no'}")


if __name__ == "__main__":
    main()
