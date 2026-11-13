"""Looks at live instances of a component in the running coverage game and prints what every
part of it looks like - the starting point for a layout hypothesis, and usable on any
component, mapped by Script Extender or not.

    python research/coverage/probe.py client ecl::unsheath::VisualStateComponent
    python research/coverage/probe.py server ls::SaveWithComponent --max 64 --json

For every 8-byte slot it says what the values are across instances (entity handles that are
alive, pointers into the executable with the symbol base's name for them, heap pointers,
plain values), and for every 4-byte half the range as integer and as float, and whether the
value is the same in all instances. Read-only.

As a module: `dump(game, side, name, max)` returns the raw instances, `survey(dump)` the
per-offset summary; check.py uses both.
"""
import argparse
import json
import sqlite3
import struct
import time
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import oldrepo  # noqa: E402
PROBE_LUA = (HERE / "lua" / "probe.lua").read_text(encoding="utf-8")
SYMBOLS = oldrepo.symbols_db()


def call(game, side: str, expression: str, timeout: float = 60):
    """Evaluates a Lua expression with the Probe library loaded."""
    for attempt in range(5):
        try:
            return game.lua(side, PROBE_LUA + "\nreturn " + expression, timeout)
        except PermissionError:  # the game is reading the command file this very moment
            if attempt == 4:
                raise
            time.sleep(0.2)


def lua_value(value) -> str:
    """A Python value (dict, list, str, number, bool, None) as a Lua literal."""
    if isinstance(value, dict):
        return "{" + ", ".join(f"[{json.dumps(k)}] = {lua_value(v)}" for k, v in value.items()) + "}"
    if isinstance(value, (list, tuple)):
        return "{" + ", ".join(lua_value(v) for v in value) + "}"
    if value is None:
        return "nil"
    if isinstance(value, bool):
        return "true" if value else "false"
    return json.dumps(value)


def dump(game, side: str, name: str, max_instances: int = 16) -> dict:
    result = call(game, side, f"Probe.Dump({json.dumps(name)}, {int(max_instances)})")
    instances = result.get("Instances") or []
    if isinstance(instances, dict):  # an empty Lua table arrives as {}
        instances = list(instances.values())
    for instance in instances:
        instance["Bytes"] = bytes.fromhex(instance["Hex"])
    result["Instances"] = instances
    return result


def plausible_float(raw: int) -> bool:
    """A 32-bit pattern that reads as an ordinary float: zero, magnitude 1e-10 .. 1e7, +-FLT_MAX, infinity or the default NaN."""
    # zero of either sign (-0.0 is what a direction component often holds); +-FLT_MAX, the
    # engine's "not set" for positions; +-infinity, which FLT_MAX turns into after one transform
    # the canonical quiet NaN is a default too (ls::SoundVirtualPositionComponent starts with it)
    if raw & 0x7FFFFFFF in (0, 0x7F7FFFFF, 0x7F800000, 0x7FC00000):
        return True
    value = struct.unpack("<f", struct.pack("<I", raw))[0]
    # 1e-10: results of arithmetic come out tiny but ordinary (4.6e-9 seen in ls::SoundCameraComponent);
    # small integers read as floats are far below that (1e-38 and less), so they still fail
    return value == value and 1e-10 <= abs(value) <= 1e7


def survey(dumped: dict) -> list[dict]:
    """One row per 4-byte step: what the instances hold there."""
    instances = dumped["Instances"]
    size = dumped.get("TotalSize") or 0
    if instances:  # a sample may hold less than the type's size (cut at a limit, or only the inline part)
        size = min(size, min(len(instance["Bytes"]) for instance in instances))
    rows = []
    for offset in range(0, size, 4):
        row = {"offset": offset}
        if offset % 8 == 0 and offset + 8 <= size:
            kinds = {}
            rvas = set()
            for instance in instances:
                slot = instance["Slots"][offset // 8]
                kinds[slot["Kind"]] = kinds.get(slot["Kind"], 0) + 1
                if slot["Kind"] == "module":
                    rvas.add(slot["Rva"])
            row["slot"] = kinds
            row["rvas"] = sorted(rvas)
        width = min(4, size - offset)
        raws = [int.from_bytes(i["Bytes"][offset:offset + width], "little") for i in instances]
        row["width"] = width
        row["distinct"] = len(set(raws))
        row["min"], row["max"] = (min(raws), max(raws)) if raws else (None, None)
        row["floats"] = width == 4 and all(plausible_float(r) for r in raws) and any(raws)
        row["samples"] = sorted(set(raws))[:6]
        row["bytes"] = [sorted({i["Bytes"][offset + b] for i in instances})[:4]
                        for b in range(width)]
        rows.append(row)
    return rows


def name_of_rva(rva: int) -> str:
    """The symbol base's name for an address in the game executable, if it has one."""
    if not SYMBOLS.exists():
        return ""
    with sqlite3.connect(SYMBOLS) as base:
        hit = base.execute(
            "SELECT s.full_name, m.method FROM matches m JOIN symbols s "
            "ON s.dll_id = m.toolkit_dll AND s.rva = m.toolkit_rva "
            "JOIN dlls d ON d.id = m.game_dll WHERE d.name = 'bg3_dx11.exe' AND m.game_rva = ? LIMIT 1",
            (rva,)).fetchone()
    return f"{hit[0]} ({hit[1]})" if hit else ""


def render(dumped: dict) -> str:
    instances = dumped["Instances"]
    lines = [f"{dumped['Name']}: {len(instances)} instances, {dumped.get('TotalSize')} bytes "
             f"(inline {dumped.get('InlineSize')})"]
    if not instances:
        return lines[0] + " - nothing alive carries it"
    for row in survey(dumped):
        text = f"  +0x{row['offset']:03X}  "
        if "slot" in row:
            text += "qword " + ",".join(f"{kind}x{count}" for kind, count in sorted(row["slot"].items()))
            for rva in row["rvas"][:2]:
                text += f" [exe+0x{rva:X} {name_of_rva(rva)}]"
            text += "  | "
        else:
            text += " " * 4 + "| "
        if row["distinct"] == 1:
            text += f"always 0x{row['samples'][0]:X}"
        else:
            text += f"{row['distinct']} values 0x{row['min']:X}..0x{row['max']:X}"
        if row["floats"]:
            floats = [struct.unpack("<f", struct.pack("<I", raw))[0] for raw in row["samples"]]
            text += "  float " + " ".join(f"{value:.4g}" for value in floats[:4])
        text += "  bytes " + " ".join("/".join(f"{b:02x}" for b in values) for values in row["bytes"])
        lines.append(text)
    return "\n".join(lines)


def attach():
    """The Game object of the running coverage instance."""
    sys.argv = [sys.argv[0]]
    import clean_stand  # noqa: F401  (selects the instance and strips foreign paks)
    import gatekit
    if not gatekit.game_pids():
        raise SystemExit("the coverage game is not running: python research/coverage/clean_stand.py up --level ...")
    return gatekit.Game()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("side", choices=["client", "server"])
    parser.add_argument("component", nargs="+")
    parser.add_argument("--max", type=int, default=16)
    parser.add_argument("--json", action="store_true")
    options = parser.parse_args()
    sys.path.insert(0, str(HERE))
    game = attach()
    for component in options.component:
        dumped = dump(game, options.side, component, options.max)
        if options.json:
            for instance in dumped["Instances"]:
                del instance["Bytes"]
            print(json.dumps(dumped))
        else:
            print(render(dumped))
