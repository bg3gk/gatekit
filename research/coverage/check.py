"""Scenario: checks the layouts in research/coverage/layouts against the running game and records
the result in the coverage index.

    python research/coverage/clean_stand.py run research/coverage/check.py            a game of its own
    python research/coverage/clean_stand.py run research/coverage/check.py --attach   the running one
    set COVERAGE_ONLY=ecl::unsheath     only layouts whose component name contains this

Two stages per layout, both read-only:

  raw     the hypothesis against memory: the game registers the type with the layout's size,
          and on every live instance each field holds what its type allows (an EntityHandle is
          null or alive, a float is an ordinary number, a bool is 0 or 1, an Array has a
          readable buffer and size <= capacity). Works on any layout, built or not.
  mapped  once the layout is in the Script Extender build: entity.<ExtName>.<Field> returns
          exactly what the raw bytes at the field's offset say, on every live instance.

A layout that passes "mapped" on at least one instance is recorded as verified in
coverage.json (stage, instances, date, this scenario). A tag has no bytes to compare: it is
verified when the game registers it as a tag and Script Extender maps it; live holders, if
any, must then give a non-nil entity.<ExtName>.

Holders are taken from the game as it stands and, unless COVERAGE_SITUATIONS is empty, from a
tick capture that runs while situations play (situations.py; default "weapons,equipment,potion,dialogue,fight,surface"), so
that one-frame and short-lived components are read in the tick they exist. Samples from the
library of seen components (seen.py) are added for the raw stage unless COVERAGE_LIBRARY=0.

On stock Script Extender (no Ext.Debug.GetComponentAddress) everything is skipped.
"""
import json
import os
import struct
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import generate  # noqa: E402
import probe  # noqa: E402
import situations  # noqa: E402

LEVEL = "GustavX:TUT_Avernus_C"
MODS: list[str] = []
INDEX = HERE / "coverage.json"
RESULTS = HERE / "work" / "check-results.json"
NULL_ENTITY = 0xFFC0000000000000
INSTANCES = 64
BATCH = 40  # layouts per call
CAPTURED_PER_TYPE = 8  # samples the tick capture keeps of one type


def read(raw: bytes, offset: int, size: int) -> int:
    return int.from_bytes(raw[offset:offset + size], "little")


def raw_problem(game, side: str, field: dict, raw: bytes, earlier: bool = False) -> str | None:
    """Why this instance's bytes cannot be a value of the field's type, or None."""
    kind, offset = field["type"], field["offset"]
    # COVERAGE_FAST: no question to the game per handle or buffer (thousands of round trips on a
    # busy level) - handles are judged by shape, as for samples taken earlier
    earlier = earlier or os.environ.get("COVERAGE_FAST") == "1"
    if kind == "bool":
        value = raw[offset]
        return None if value in (0, 1) else f"bool holds {value}"
    if kind == "float":
        bits = read(raw, offset, 4)
        return None if probe.plausible_float(bits) else f"float bits 0x{bits:08X}"
    if kind in ("glm::vec2", "glm::vec3", "glm::vec4", "glm::quat"):
        count = generate.PRIMITIVES[kind][0] // 4
        bad = [i for i in range(count) if not probe.plausible_float(read(raw, offset + 4 * i, 4))]
        return f"{kind} part {bad[0]} is not an ordinary float" if bad else None
    if kind == "EntityHandle":
        value = read(raw, offset, 8)
        if value in (NULL_ENTITY, 0):
            return None
        signed = value - (1 << 64) if value >= 1 << 63 else value
        # A handle is "index | salt << 32 | type << 54". One that is not alive is still accepted
        # when it has that shape: the game does leave dangling handles (seen: the parent of a
        # cutscene dummy's attachment after the dialogue ended), and a captured sample is judged
        # after what it pointed to may be gone. Two floats or a pointer do not have the shape.
        shaped = (value >> 32) & 0x3FFFFF != 0 and (value >> 54) < 0x400 and (value & 0xFFFFFFFF) < 0x1000000
        if earlier:
            return None if shaped else f"EntityHandle 0x{value:016X} is not shaped like a handle"
        looks = probe.call(game, side, f"Probe.Classify({signed}).Kind")
        return None if looks == "entity" or shaped else f"EntityHandle 0x{value:016X} is {looks}, not an entity handle"
    if kind.startswith("std::array<EntityHandle,"):
        for i in range(generate.type_size(kind)[0] // 8):
            problem = raw_problem(game, side, {"type": "EntityHandle", "offset": offset + 8 * i}, raw, earlier)
            if problem:
                return f"[{i}] {problem}"
        return None
    if kind.startswith(("HashSet<", "HashMap<")):
        # buckets (pointer, count), next-index array (pointer, capacity, size), key array (pointer,
        # capacity, size); a map adds its value array (pointer, count)
        buckets, bucket_count = read(raw, offset, 8), read(raw, offset + 8, 4)
        next_size = read(raw, offset + 0x1C, 4)
        keys, key_capacity, key_size = read(raw, offset + 0x20, 8), read(raw, offset + 0x28, 4), read(raw, offset + 0x2C, 4)
        if key_size > key_capacity:
            return f"{kind}: {key_size} keys in a capacity of {key_capacity}"
        if next_size != key_size:
            return f"{kind}: {key_size} keys but {next_size} chain links"
        if key_size and bucket_count == 0:
            return f"{kind}: {key_size} keys and no buckets"
        for label, pointer, used in (("buckets", buckets, bucket_count), ("keys", keys, key_capacity)):
            if used and (earlier is False) and probe.call(game, side, f"Probe.Read({pointer}, 1)") is None:
                return f"{kind}: the {label} at 0x{pointer:X} are not readable"
        return None
    if kind.startswith("Array<"):
        pointer, capacity, size = read(raw, offset, 8), read(raw, offset + 8, 4), read(raw, offset + 12, 4)
        if size > capacity:
            return f"Array size {size} > capacity {capacity}"
        if capacity and not earlier and probe.call(game, side, f"Probe.Read({pointer}, 1)") is None:
            return f"Array buffer 0x{pointer:X} is not readable"
        return None
    return None  # integers, FixedString, Guid: any bit pattern is a value


def expected(field: dict, raw: bytes):
    """What Script Extender should return for the field, from the raw bytes; None = not compared."""
    kind, offset = field["type"], field["offset"]
    if "void*" in kind:
        return None  # generate.py hides raw pointers from Lua: there is no property to compare
    if kind == "bool":
        return raw[offset] != 0
    if kind == "float":
        return struct.unpack_from("<f", raw, offset)[0]
    if kind == "EntityHandle":
        return {"Entity": read(raw, offset, 8)}
    if kind.startswith("Array<"):
        return {"Length": read(raw, offset + 12, 4)}
    if kind.startswith(("HashSet<", "HashMap<")):
        return {"Length": read(raw, offset + 44, 4)}  # the size of its key array
    if kind.startswith("std::array<EntityHandle,"):
        count = generate.type_size(kind)[0] // 8
        return {"Handles": [read(raw, offset + 8 * i, 8) for i in range(count)]}
    if kind == "UserId":
        return read(raw, offset, 4)
    if kind in generate.PRIMITIVES and kind.endswith("_t"):
        size = generate.PRIMITIVES[kind][0]
        value = read(raw, offset, size)
        if kind.startswith("int") and value >= 1 << (8 * size - 1):
            value -= 1 << (8 * size)
        return value
    return None


def same(want, got) -> bool:
    if isinstance(want, bool):
        return want == got
    if isinstance(want, float):
        return isinstance(got, (int, float)) and (want == got or abs(want - got) <= 1e-6 * max(1.0, abs(want)))
    if isinstance(want, dict) and "Entity" in want:
        if got is None:  # Script Extender gives nil for the null handle
            return want["Entity"] in (NULL_ENTITY, 0)
        return isinstance(got, dict) and (got.get("Entity", 0) & (2 ** 64 - 1)) == want["Entity"]
    if isinstance(want, dict) and "Handles" in want:
        items = got.get("Items", []) if isinstance(got, dict) else []
        if isinstance(items, dict):
            items = [items.get(str(i + 1)) for i in range(len(want["Handles"]))]
        items = list(items) + [None] * (len(want["Handles"]) - len(items))  # trailing nils are dropped
        return all(same({"Entity": handle}, item) for handle, item in zip(want["Handles"], items))
    if isinstance(want, dict) and "Length" in want:
        return isinstance(got, dict) and want["Length"] in (got.get("Length"), got.get("Count"))
    if isinstance(want, int) and isinstance(got, int) and not isinstance(want, bool):
        return (want - got) % (1 << 64) == 0  # Lua integers are signed: 0xFFFF...FF comes back as -1
    return want == got


def specs_for(layouts: dict, registry: dict, side: str) -> list[dict]:
    """What the Lua side needs to read a layout's component: engine name, Script Extender name, fields."""
    return [{"Name": name, "ExtName": layout["ext_name"],
             "Fields": [f["name"] for f in layout.get("fields", [])
                        if not generate.is_padding(f) and "void*" not in f["type"]]}
            for name, layout in layouts.items() if name in registry[side]]


def as_list(value) -> list:
    """A Lua array as a list (an empty Lua table arrives as {})."""
    return list(value.values()) if isinstance(value, dict) else (value or [])


def read_live(game, layouts: dict, registry: dict) -> dict:
    """The holders alive right now, raw and through Script Extender, in a few calls per side:
    {side: {component: rows}}."""
    rows = {"client": {}, "server": {}}
    for side in rows:
        specs = specs_for(layouts, registry, side)
        for start in range(0, len(specs), BATCH):
            part = probe.call(game, side, f"Probe.MappedMany({probe.lua_value(specs[start:start + BATCH])}, "
                                          f"{INSTANCES})", timeout=120)
            for name, found in (part or {}).items():
                rows[side][name] = as_list(found)
    return rows


def check_layout(game, layout: dict, registry: dict, live: dict) -> dict:
    """`live` holds the rows to judge: {side: {component: [{Entity, Hex, Present, Values}]}}."""
    name = layout["component"]
    result = {"component": name, "raw": None, "mapped": None, "instances": 0, "read_through_se": 0, "nonzero": False,
              "problems": []}
    sides = [side for side in ("client", "server") if name in registry[side]]
    if not sides:
        result["problems"].append("the game does not register this type")
        result["raw"] = False
        return result
    fields = [f for f in layout.get("fields", []) if not generate.is_padding(f)]
    raw_ok, mapped_ok, mapped_seen = True, True, False
    for side in sides:
        entry = registry[side][name]
        if layout.get("tag"):
            if (entry.get("TotalSize") or 0) > 1:
                raw_ok = False
                result["problems"].append(f"{side}: registered with {entry['TotalSize']} bytes, not a tag")
        elif entry.get("TotalSize") != layout["size"]:
            raw_ok = False
            result["problems"].append(f"{side}: registered size {entry.get('TotalSize')}, layout says {layout['size']}")
            continue
        rows = live[side].get(name, [])
        result["instances"] += len(rows)
        if entry["Mapped"]:
            mapped_seen = True
        for row in rows:
            raw = bytes.fromhex(row.get("Hex") or "")
            if any(raw):
                result["nonzero"] = True
            for field in fields if raw else []:
                problem = raw_problem(game, side, field, raw, earlier="Tick" in row)
                if problem:
                    raw_ok = False
                    result["problems"].append(f"{side} entity {row['Entity']}: {field['name']}: {problem}")
            if not entry["Mapped"] or row.get("Library"):
                continue  # a sample from the library was not read through Script Extender
            if not row["Present"]:
                mapped_ok = False
                result["problems"].append(f"{side} entity {row['Entity']}: entity.{layout['ext_name']} is nil")
                continue
            result["read_through_se"] += 1
            values = row.get("Values") or {}
            for field in fields if raw else []:
                want = expected(field, raw)
                if want is not None and not same(want, values.get(field["name"])):
                    mapped_ok = False
                    result["problems"].append(
                        f"{side} entity {row['Entity']}: {field['name']}: bytes say {want}, "
                        f"Script Extender says {values.get(field['name'])}")
    result["raw"] = raw_ok
    result["mapped"] = (mapped_ok and raw_ok) if mapped_seen else None
    result["problems"] = result["problems"][:12]
    return result


def record(results: list[dict], layouts: dict) -> int:
    """Writes `verified` into coverage.json for what passed the mapped stage."""
    index = json.loads(INDEX.read_text(encoding="utf-8"))
    passed = {}
    for result in results:
        tag = layouts[result["component"]].get("tag")
        # Without a live holder nothing was read through the layout: mapped, but not verified.
        if result["mapped"] and result["read_through_se"] > 0:
            passed[result["component"]] = {
                "date": time.strftime("%Y-%m-%d"), "scenario": "research/coverage/check.py",
                "stage": ("one byte registered, entity.<Name> non-nil on every live holder" if tag
                          else "mapped fields equal raw memory"),
                "instances": result["read_through_se"],
            }
    for item in index["components"]:
        if item["name"] in passed and item.get("inlineSize") is not None:
            item["verified"] = passed[item["name"]]
    INDEX.write_text(json.dumps(index, ensure_ascii=False, indent=1), encoding="utf-8")
    return len(passed)


def capture_during(game, layouts: dict, registry: dict, wanted: list[str]) -> dict:
    """Runs the situations with the tick capture on and returns its samples per side: holders
    that appeared at any tick, read (raw and through Script Extender) in that same tick."""
    for side in ("client", "server"):
        specs = specs_for(layouts, registry, side)
        probe.call(game, side, f"Probe.CaptureStart({probe.lua_value(specs)}, {CAPTURED_PER_TYPE})", timeout=60)
    try:
        situations.run(game, wanted)
    finally:
        samples = {}
        for side in ("client", "server"):
            probe.call(game, side, "Probe.CaptureStop()")
            samples[side] = {}
            names = [spec["Name"] for spec in specs_for(layouts, registry, side)]
            for start in range(0, len(names), BATCH * 4):  # the answer is fetched in parts
                part = probe.call(game, side, "(function() local all, out = Probe.CaptureResult().Samples, {} "
                                              f"for _, name in ipairs({probe.lua_value(names[start:start + BATCH * 4])}) do "
                                              "if all[name] and #all[name] > 0 then out[name] = all[name] end end "
                                              "return out end)()", timeout=120)
                for name, rows in (part or {}).items():
                    samples[side][name] = as_list(rows)
    return samples


def add_captured(live: dict, captured: dict) -> None:
    """Adds captured samples to the live rows, one row per entity."""
    for side, by_name in captured.items():
        for name, rows in by_name.items():
            known = {row["Entity"] for row in live[side].get(name, [])}
            live[side].setdefault(name, []).extend(row for row in rows if row["Entity"] not in known)


def add_library(live: dict, layouts: dict, registry: dict) -> int:
    """Adds the samples of the library of seen components (seen.py) to the rows to judge: raw
    bytes taken from the running game earlier, in whatever situation brought the type to life.
    They count for the raw stage only."""
    import seen
    added = 0
    for name, layout in layouts.items():
        for side in ("client", "server"):
            found = seen.samples(name, side) if name in registry[side] else None
            if not found:
                continue
            known = {row["Entity"] for row in live[side].get(name, [])}
            for instance in found["Instances"]:
                if instance["Entity"] in known or len(instance["Bytes"]) < layout.get("size", 0):
                    continue  # already read live, or a sample cut short of the type's size
                live[side].setdefault(name, []).append({"Entity": instance["Entity"], "Hex": instance["Hex"],
                                                        "Tick": -1, "Library": True, "Present": False, "Values": {}})
                added += 1
    return added


def run(game):
    if not game.client("return Ext.Debug.GetComponentAddress ~= nil"):
        print("SKIP: stock Script Extender (no Ext.Debug.GetComponentAddress)")
        return
    only = os.environ.get("COVERAGE_ONLY", "")
    layouts = {}
    for layout in generate.load(only_confirmed=False):
        if only not in layout["component"]:
            continue
        try:
            generate.validate(layout)
        except Exception as problem:
            print(f"  not checked, the layout file is not valid: {problem}")
            continue
        layouts[layout["component"]] = layout
    registry = {}
    for side in ("client", "server"):
        registry[side] = {}
        for entry in probe.call(game, side, "Ext.Debug.GetComponentTypes()"):
            if entry.get("TotalSize") is not None:
                registry[side][entry["Name"]] = entry
    live = read_live(game, layouts, registry)
    wanted = [name for name in os.environ.get("COVERAGE_SITUATIONS", "weapons,equipment,potion,dialogue,fight,surface").split(",") if name]
    if wanted:
        add_captured(live, capture_during(game, layouts, registry, wanted))
    if os.environ.get("COVERAGE_LIBRARY", "1") != "0":
        print(f"  {add_library(live, layouts, registry)} samples added from the library of seen components")
    results = [check_layout(game, layout, registry, live) for layout in layouts.values()]
    RESULTS.parent.mkdir(exist_ok=True)
    RESULTS.write_text(json.dumps(results, indent=1), encoding="utf-8")
    stage = {True: "ok", False: "FAIL", None: "-"}
    for result in results:
        print(f"  raw {stage[result['raw']]:4} mapped {stage[result['mapped']]:4} "
              f"{result['instances']:3} instances  {result['component']}")
        for problem in result["problems"]:
            print(f"        {problem}")
    verified = record(results, layouts)
    print(f"{len(results)} layouts checked, {verified} recorded as verified in coverage.json")
    # A hypothesis the game contradicts is a finding; a confirmed layout it contradicts is a failure.
    failed = [r["component"] for r in results if (r["raw"] is False or r["mapped"] is False)
              and layouts[r["component"]].get("status") == "confirmed"]
    assert not failed, f"confirmed layouts contradicted by the game: {failed}"
