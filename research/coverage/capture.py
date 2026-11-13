"""Samples components that live for a frame or a moment while situations play, and prints
them like probe.py does - the raw material for a layout hypothesis of a type that is never
alive in an idle level.

    python research/coverage/capture.py --situations weapons,dialogue esv::unsheath::DefaultChangedOneFrameComponent ...
    python research/coverage/capture.py --situations weapons --names-file list.json --out work/captured.txt
    python research/coverage/capture.py --situations weapons,dialogue --census work/census.json

With --census it also writes which component types were seen at any tick, per side, with the
highest number of holders - what a situation brings to life. Read-only; needs the GateKit build.
"""
import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import probe  # noqa: E402
import situations  # noqa: E402

PER_TYPE = 16


def capture(game, names: list[str], wanted: list[str], census: bool = False) -> dict:
    """{side: {"Samples": {name: dump-like dict}, "Seen": {name: {Max, FirstTick}}, "Ticks": n}}"""
    specs = [{"Name": name} for name in names]
    for side in ("client", "server"):
        probe.call(game, side, f"Probe.CaptureStart({probe.lua_value(specs)}, {PER_TYPE})", timeout=60)
        if census:
            probe.call(game, side, "Probe.CaptureCensus()")
    try:
        situations.run(game, wanted)
    finally:
        out = {}
        for side in ("client", "server"):
            probe.call(game, side, "Probe.CaptureStop()")
            result = probe.call(game, side, "Probe.CaptureResult()", timeout=120)
            samples = {}
            for name, rows in (result.get("Samples") or {}).items():
                rows = list(rows.values()) if isinstance(rows, dict) else rows
                rows = [row for row in rows if row.get("Hex")]
                if not rows:
                    continue
                size = len(rows[0]["Hex"]) // 2
                instances = []
                for row in rows:
                    raw = bytes.fromhex(row["Hex"])
                    instances.append({"Entity": row["Entity"], "Hex": row["Hex"], "Bytes": raw, "Tick": row["Tick"],
                                      "Slots": classify(game, side, raw)})
                samples[name] = {"Name": name, "TotalSize": size, "InlineSize": size, "Instances": instances}
            out[side] = {"Samples": samples, "Seen": result.get("Seen") or {}, "Ticks": result.get("Ticks")}
    return out


def classify(game, side: str, raw: bytes) -> list[dict]:
    """The probe's 8-byte slot classes for bytes captured earlier (entities may be gone since)."""
    values = [int.from_bytes(raw[o:o + 8], "little", signed=True) for o in range(0, len(raw) - 7, 8)]
    if not values:
        return []
    kinds = probe.call(game, side, "(function() local out = {} "
                                   f"for i, v in ipairs({probe.lua_value(values)}) do out[i] = Probe.Classify(v) end "
                                   "return out end)()")
    kinds = list(kinds.values()) if isinstance(kinds, dict) else kinds
    for index, slot in enumerate(kinds):
        slot["Offset"] = index * 8
    return kinds


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("components", nargs="*")
    parser.add_argument("--names-file", help="a JSON list of component names")
    parser.add_argument("--situations", default="weapons,dialogue")
    parser.add_argument("--out", help="write the rendered samples here instead of printing")
    parser.add_argument("--census", help="write what was seen at any tick to this JSON file")
    options = parser.parse_args()
    names = list(options.components)
    if options.names_file:
        names += json.loads(Path(options.names_file).read_text(encoding="utf-8"))
    game = probe.attach()
    captured = capture(game, names, options.situations.split(","), census=bool(options.census))
    text = []
    for side, result in captured.items():
        text.append(f"=== {side}: {result['Ticks']} ticks, situations {options.situations} ===")
        for name in names:
            if name in result["Samples"]:
                text.append(probe.render(result["Samples"][name]))
        missing = [name for name in names if name not in result["Samples"]]
        text.append(f"--- not seen on the {side}: {len(missing)} of {len(names)}")
    rendered = "\n".join(text)
    if options.out:
        Path(options.out).write_text(rendered, encoding="utf-8")
        print(f"wrote {options.out}")
    else:
        print(rendered)
    if options.census:
        Path(options.census).write_text(json.dumps({side: result["Seen"] for side, result in captured.items()}, indent=1),
                                        encoding="utf-8")
