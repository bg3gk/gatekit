"""The library of seen components: a standing capture of whatever comes to life in the
running game, kept on disk per component type.

    python research/coverage/seen.py start [label]     begin capturing on both sides
    python research/coverage/seen.py mark <label>      name what happens from now on
    python research/coverage/seen.py drain             move new samples into the library
    python research/coverage/seen.py summary           what the library holds, against the index
    python research/coverage/seen.py show <component>  the samples of one type, like probe.py prints them
    python research/coverage/seen.py context <component>   what was going on when it came to life: level,
                                                      what the step did, the types born in the same step

Unlike capture.py, which samples a prepared list of types during prepared situations, this
keeps the first few distinct instances of every type that has holders at any tick
(lua/probe.lua, Probe.Seen*). Whatever is done in the game while it runs feeds the library.

Library: research/coverage/seen/<side>/<type>.json (raw game memory: not committed) and
seen/index.json: { type: { side: { max, labels, levels, samples } } }.

As a module: start(game, label), mark(game, label), drain(game), reset(game) (drain, world
reset, start again), samples(name, side).
"""
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import probe  # noqa: E402

LIBRARY = HERE / "seen"
INDEX = LIBRARY / "index.json"
SIDES = ("client", "server")


def file_of(side: str, name: str) -> Path:
    return LIBRARY / side / (re.sub(r"[^A-Za-z0-9_.]+", "_", name) + ".json")


def load_index() -> dict:
    return json.loads(INDEX.read_text(encoding="utf-8")) if INDEX.exists() else {}


def layout_specs() -> tuple[dict, dict]:
    """({engine name: {ExtName, Fields}}, {engine name: layout}) for the layouts that are in the
    Script Extender build: these are read through it in the tick they are sampled."""
    import generate
    specs, layouts = {}, {}
    for layout in generate.load(only_confirmed=True):
        if layout.get("tag"):
            continue
        fields = [f["name"] for f in layout.get("fields", []) if not generate.is_padding(f) and "void*" not in f["type"]]
        specs[layout["component"]] = {"ExtName": layout["ext_name"], "Fields": fields}
        layouts[layout["component"]] = layout
    return specs, layouts


def start(game, label: str = "idle", per_label: int = 3, verify: bool = True) -> None:
    """Begins the capture on both sides. With `verify`, every type that has a layout in the
    build is also read through Script Extender in the tick it is sampled; drain() compares the
    two readings and records the types that agree as verified."""
    specs = layout_specs()[0] if verify else {}
    for side in SIDES:
        options = f"{{ Label = {json.dumps(label)}, PerLabel = {per_label}, Specs = {probe.lua_value(specs)} }}"
        probe.call(game, side, f"Probe.SeenStart({options})", timeout=180)


def mark(game, label: str) -> None:
    for side in SIDES:
        probe.call(game, side, f"Probe.SeenMark({json.dumps(label)})")


def level_of(game) -> str:
    try:
        return game.server("return GK.Stage.CurrentLevel()") or ""
    except Exception:  # noqa: BLE001 - the level name is a courtesy
        return ""


def new_names(game) -> set[str]:
    """Types that came to life since the last call, on either side - one small call per side."""
    names = set()
    for side in SIDES:
        found = probe.call(game, side, "Probe.SeenNew()", timeout=60) or []
        names.update(found.values() if isinstance(found, dict) else found)
    return names


class library_lock:
    """One writer of the library at a time: tours may run on several copies of the game at once."""

    def __enter__(self):
        import os
        import time
        LIBRARY.mkdir(exist_ok=True)
        self.path = LIBRARY / ".lock"
        for _ in range(600):
            try:
                self.handle = os.open(self.path, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                return self
            except FileExistsError:
                # a lock left by a process that died is not held for ever
                try:
                    if time.time() - self.path.stat().st_mtime > 120:
                        self.path.unlink()
                except OSError:
                    pass
                time.sleep(0.1)
        raise TimeoutError("the library of seen components stays locked")

    def __exit__(self, *problem):
        import os
        os.close(self.handle)
        self.path.unlink(missing_ok=True)


def drain(game) -> dict:
    """Moves queued samples into the library; returns {side: number of new samples}."""
    with library_lock():
        return _drain(game)


def _drain(game) -> dict:
    index, level, counts = load_index(), level_of(game), {}
    for side in SIDES:
        added: dict[str, list] = {}
        while True:
            got = probe.call(game, side, "Probe.SeenDrain(120)", timeout=120)
            rows = got.get("Rows") or []
            rows = list(rows.values()) if isinstance(rows, dict) else rows
            for row in rows:
                added.setdefault(row["Name"], []).append(row)
            if not got.get("Left"):
                break
        compare(side, added)
        for name, rows in added.items():
            path = file_of(side, name)
            path.parent.mkdir(parents=True, exist_ok=True)
            if path.exists():
                kept = json.loads(path.read_text(encoding="utf-8"))
            else:
                kept = {"component": name, "side": side, "size": rows[0]["Size"], "samples": []}
            have = {sample["hex"] for sample in kept["samples"]}
            for row in rows:
                if row["Hex"] not in have and len(kept["samples"]) < 48:
                    have.add(row["Hex"])
                    kept["samples"].append({"hex": row["Hex"], "entity": row["Entity"], "label": row["Label"],
                                            "level": level})
            path.write_text(json.dumps(kept, indent=1), encoding="utf-8")
        types = probe.call(game, side, "Probe.SeenTypes()", timeout=120) or {}
        for name, seen in types.items():
            entry = index.setdefault(name, {}).setdefault(side, {"max": 0, "labels": [], "levels": [], "samples": 0})
            entry["max"] = max(entry["max"], seen["Max"])
            if seen["Label"] not in entry["labels"]:
                entry["labels"].append(seen["Label"])
            if level and level not in entry["levels"]:
                entry["levels"].append(level)
            if name in added:
                entry["samples"] = len(json.loads(file_of(side, name).read_text(encoding="utf-8"))["samples"])
        counts[side] = sum(len(rows) for rows in added.values())
    LIBRARY.mkdir(exist_ok=True)
    INDEX.write_text(json.dumps(index, indent=1, sort_keys=True), encoding="utf-8")
    return counts


MISMATCHES = HERE / "work" / "tick-mismatches.json"


def compare(side: str, added: dict) -> None:
    """For rows that were also read through Script Extender: every field must return what the
    raw bytes of the same tick say. Types that agree on every row are recorded as verified in
    coverage.json; a disagreement is written to work/tick-mismatches.json (and never verified)."""
    import time
    import check
    import generate
    layouts = layout_specs()[1]
    passed, failed = {}, {}
    for name, rows in added.items():
        layout = layouts.get(name)
        if layout is None:
            continue
        fields = [f for f in layout.get("fields", []) if not generate.is_padding(f)]
        for row in rows:
            if not row.get("Present") or not row.get("Hex"):
                continue
            raw = bytes.fromhex(row["Hex"])
            if len(raw) < layout["size"]:
                continue  # the sample was cut short of the type
            values = row.get("Values") or {}
            bad = None
            for field in fields:
                want = check.expected(field, raw)
                if want is not None and not check.same(want, values.get(field["name"])):
                    bad = f"{field['name']}: bytes say {want}, Script Extender says {values.get(field['name'])}"
                    break
            if bad:
                failed[name] = f"{side} entity {row['Entity']} ({row.get('Label')}): {bad}"
            else:
                passed[name] = passed.get(name, 0) + 1
    for name in failed:
        passed.pop(name, None)
    if failed:
        known = json.loads(MISMATCHES.read_text(encoding="utf-8")) if MISMATCHES.exists() else {}
        known.update(failed)
        MISMATCHES.parent.mkdir(exist_ok=True)
        MISMATCHES.write_text(json.dumps(known, indent=1, ensure_ascii=False), encoding="utf-8")
    if passed:
        index_path = HERE / "coverage.json"
        index = json.loads(index_path.read_text(encoding="utf-8"))
        for item in index["components"]:
            if item["name"] in passed and item["side"] == side and item.get("mapped") and not item.get("verified"):
                item["verified"] = {"date": time.strftime("%Y-%m-%d"), "scenario": "research/coverage/seen.py",
                                    "stage": "mapped fields equal raw memory, read in the tick of capture",
                                    "instances": passed[item["name"]]}
        index_path.write_text(json.dumps(index, ensure_ascii=False, indent=1), encoding="utf-8")


def reset(game, label: str = "idle") -> None:
    """A world reset that loses nothing: drain, reset, start capturing again."""
    drain(game)
    game.reset()
    start(game, label)


def samples(name: str, side: str) -> dict | None:
    """The library's samples of a type in the shape probe.dump() returns (without slot classes)."""
    path = file_of(side, name)
    if not path.exists():
        return None
    kept = json.loads(path.read_text(encoding="utf-8"))
    instances = [{"Entity": s["entity"], "Hex": s["hex"], "Bytes": bytes.fromhex(s["hex"]), "Label": s["label"]}
                 for s in kept["samples"]]
    return {"Name": name, "TotalSize": kept["size"], "InlineSize": kept["size"], "Instances": instances}


def offline_kind(value: int) -> dict:
    """What a 64-bit value from a stored sample looks like, judged without the game: "zero",
    "module" (an address in the 0x7FF... range where the executable and DLLs are loaded; Rva is
    not known), "heap" (shaped like a user-mode pointer) or "value". Liveness of entity
    handles cannot be told offline."""
    if value == 0:
        return {"Kind": "zero"}
    if value >> 40 == 0x7FF:
        return {"Kind": "module", "Rva": value & 0xFFFFFFF}
    if 0x10000000000 <= value < 0x7FF0000000000 and value % 8 == 0:
        return {"Kind": "heap"}
    return {"Kind": "value"}


def context(name: str) -> str:
    """What was going on when a type came to life: for every situation its samples were taken
    in - the level, what the tour step did (its Lua), and which other types appeared for the
    first time in that same step (types born together usually belong to one system)."""
    index = load_index().get(name) or {}
    labels, lines = [], [f"{name}"]
    for side, info in index.items():
        lines.append(f"  {side}: at most {info['max']} holders at once; levels {', '.join(info['levels']) or '?'}; "
                     f"first seen under: {', '.join(info['labels'])}")
        labels += info["labels"]
        kept = file_of(side, name)
        if kept.exists():
            labels += [sample["label"] for sample in json.loads(kept.read_text(encoding="utf-8"))["samples"]]
    steps, born = {}, {}
    for tour in (HERE / "tours").glob("*.json"):
        for step in json.loads(tour.read_text(encoding="utf-8")):
            steps[step["label"]] = step
    for log in (HERE / "work").glob("tour-*.json"):
        for entry in json.loads(log.read_text(encoding="utf-8")):
            born.setdefault(entry["label"], []).extend(entry.get("new") or [])
    labels = list(dict.fromkeys(labels))  # in order, once each
    if len(labels) > 6:
        lines.append(f"  ({len(labels)} situations in all; the first six are shown)")
    for label in labels[:6]:
        lines.append(f"  situation \"{label}\":")
        step = steps.get(label)
        if step:
            for act in [step] + list(step.get("then") or []):
                for side in ("server", "ui", "client"):
                    if act.get(side):
                        lines.append(f"      {side}: {act[side][:230]}")
        elif ":" in label:
            kind, what = label.split(":", 1)
            lines.append(f"      a step taken from the level itself: {kind} -> {what} "
                         "(dialogs: the dialogue was started and answered through; enemies: a fight of a few turns; "
                         "event/scene/...: the host was put inside that trigger zone)")
        elif label == "idle":
            lines.append("      nothing was being done: the type lives in an idle level")
        together = [other for other in born.get(label, []) if other != name]
        if name in born.get(label, []):
            lines.append(f"      first appeared in this step, together with {len(together)} other types:")
            lines += [f"        {other}" for other in sorted(together)[:40]]
    return "\n".join(lines)


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    command, rest = sys.argv[1], sys.argv[2:]
    if command == "summary":
        index = load_index()
        print(f"the library holds {len(index)} types")
        return 0
    if command == "context":
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        print(context(rest[0]))
        return 0
    if command == "show":
        for side in SIDES:
            found = samples(rest[0], side)
            if found:
                for instance in found["Instances"]:
                    instance["Slots"] = [offline_kind(int.from_bytes(instance["Bytes"][o:o + 8], "little"))
                                         for o in range(0, len(instance["Bytes"]) - 7, 8)]
                labels = sorted({instance["Label"] for instance in found["Instances"]})
                print(f"labels: {', '.join(labels)}")
                print(side, probe.render(found))
        return 0
    game = probe.attach()
    if command == "start":
        start(game, rest[0] if rest else "idle")
    elif command == "mark":
        mark(game, rest[0])
    elif command == "drain":
        print(drain(game))
    else:
        print(__doc__)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
