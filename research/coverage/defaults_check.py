"""Checks every layout against the default constructor of the macOS build - an offline test
that needs no live instance, so it also reaches types no situation has brought to life.

    python research/coverage/defaults_check.py            all layouts
    python research/coverage/defaults_check.py timeline   one folder under layouts

`ecs::ComponentOps<T>::DefaultConstructComponents` (see macos_defaults.py) initialises a new
component member by member. Each store it makes is a fact about the layout: "N bytes are
written at offset X". A layout agrees with a store when the store begins where a member
begins and ends where a member ends (one store may initialise several adjacent members, e.g.
two 8-byte halves of a Guid with one 16-byte store). A store that begins or ends inside a
member contradicts the layout - unless the member is itself a structure (a container, a Guid,
a vector), whose parts are initialised one by one.

HOW MUCH THIS TEST IS WORTH (measured 2026-11-12): very little as a confirmation. Constructors
mostly write in coarse blocks (a whole 8- or 16-byte store, a zeroed range), so a layout that
wrongly splits or merges members inside a block still "agrees": of 52 layouts deliberately
spoiled by splitting one member in two, the test caught none. "agrees" therefore only means
"not contradicted at the coarse level"; it never promotes a hypothesis. A "contradicted",
on the other hand, is real evidence against a layout. The constructors' strong use is
elsewhere: telling tags from one-byte data (tag_evidence.py) and showing default values.

Result per layout: "agrees" (with how many stores), "contradicted" (which store), or "no
evidence" (one-frame types, constructors that only call memset or another constructor).
Written to work/defaults-check.json; a contradiction on a confirmed layout is printed loudly.
"""
import json
import re
import sys
from collections import Counter
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import generate  # noqa: E402

DEFAULTS = HERE.parent / "reference" / "macos-symbols" / "defaults.json"
RESULT = HERE / "work" / "defaults-check.json"

OPERANDS = re.compile(r"^(\w+) (.+?), \[(x\d+)(?:, #(-?0x[0-9a-f]+|-?\d+))?\]$")
# plain members whose inside a store may not split; everything else is a structure with parts
ATOMIC = {"bool", "uint8_t", "int8_t", "uint16_t", "int16_t", "uint32_t", "int32_t", "float",
          "uint64_t", "int64_t", "double", "EntityHandle", "FixedString", "void*", "UserId", "PlayerId"}


def register_width(register: str) -> int:
    return {"w": 4, "x": 8, "s": 4, "d": 8, "q": 16, "b": 1, "h": 2}.get(register[0], 0)


def stores_of(found: dict) -> list[tuple[int, int]]:
    """[(offset, width)] of the stores made through the pointer to the new component."""
    parsed = []
    for text in found.get("stores", []):
        match = OPERANDS.match(text)
        if not match:
            continue
        mnemonic, registers, base, offset = match.groups()
        offset = int(offset, 0) if offset else 0
        names = [r.strip() for r in registers.split(",")]
        if mnemonic in ("strb", "sturb"):
            width = 1
        elif mnemonic in ("strh", "sturh"):
            width = 2
        else:
            width = sum(register_width(r) for r in names)
        if width:
            parsed.append((base, offset, width, all(r in ("wzr", "xzr") for r in names)))
    if not parsed:
        return []
    # the component pointer is the base register most stores go through
    base = Counter(b for b, _, _, _ in parsed).most_common(1)[0][0]
    mine = [(offset, width, zero) for b, offset, width, zero in parsed if b == base and offset >= 0]
    # Zeroing is often done with overlapping stores (8 bytes at +4, then 8 bytes at +9, to clear
    # +4..+17): only the union of the zero stores says anything about boundaries.
    zeros, merged = sorted((o, o + w) for o, w, zero in mine if zero), []
    for start, end in zeros:
        if merged and start <= merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], end)
        else:
            merged.append([start, end])
    return sorted({(o, w) for o, w, zero in mine if not zero} | {(start, end - start) for start, end in merged})


def judge(layout: dict, stores: list[tuple[int, int]]) -> tuple[str, str]:
    local = generate.local_types(layout)
    members = []  # (start, end, atomic)
    for field in layout["fields"]:
        size, _ = generate.type_size(field["type"], local)
        members.append((field["offset"], field["offset"] + size, field["type"] in ATOMIC or generate.is_padding(field)))
    starts = {start for start, _, _ in members}
    ends = {end for _, end, _ in members}
    used = 0
    for offset, width in stores:
        if offset >= layout["size"]:
            continue  # belongs to the storage, not to this component
        inside = [m for m in members if m[0] <= offset < m[1]]
        if inside and not inside[0][2]:
            used += 1
            continue  # a part of a structured member: its own layout is not ours to judge here
        end = offset + width
        last = [m for m in members if m[0] < end <= m[1]]
        if last and not last[0][2]:
            used += 1
            continue
        if offset not in starts or end not in ends:
            return "contradicted", f"the constructor stores {width} bytes at +0x{offset:X}, which does not match member boundaries"
        used += 1
    return ("agrees", f"{used} stores") if used else ("no evidence", "")


def main() -> int:
    defaults = json.loads(DEFAULTS.read_text(encoding="utf-8"))
    folders = sys.argv[1:]
    results, tally = {}, Counter()
    for layout in generate.load(only_confirmed=False):
        path = layout["_path"]
        if folders and path.parent.name not in folders:
            continue
        if layout.get("tag"):
            continue
        name = layout["component"]
        found = defaults.get(name)
        try:
            generate.validate(layout)
        except Exception:
            verdict, why = "invalid layout file", ""
        else:
            if found is None or found["instructions"] <= 3:
                verdict, why = "no evidence", "never default-constructed (one-frame or similar)"
            else:
                stores = stores_of(found)
                verdict, why = judge(layout, stores) if stores else ("no evidence", "the constructor only calls")
        results[name] = {"verdict": verdict, "why": why, "status": layout.get("status")}
        tally[(layout.get("status"), verdict)] += 1
        if verdict == "contradicted":
            loud = "CONFIRMED LAYOUT " if layout.get("status") == "confirmed" else ""
            print(f"{loud}contradicted: {name}: {why}")
    RESULT.parent.mkdir(exist_ok=True)
    RESULT.write_text(json.dumps(results, indent=1), encoding="utf-8")
    for key, count in sorted(tally.items(), key=str):
        print(f"{key[0] or '-':11} {key[1]:20} {count}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
